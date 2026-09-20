using System.Buffers.Text;
using System.Security.Cryptography;

namespace TproxyRelay;

public static class BridgePage
{
    private const string Template = """
<!doctype html>
<html>
<head><meta charset="utf-8"><title></title></head>
<body>
<script nonce="{{NONCE}}">
(function(){
'use strict';
var MODE='{{MODE}}';
var LANES=(MODE==='https-lanes'||MODE==='websocket-lanes');
var UP_LIMIT=2*1024*1024;
var PENDING_LIMIT=32*1024*1024;
var pending=[];
var pendingBytes=0;
var queueWaiter=null;
var firstBuffer=null;
var firstReady=null;
var started=false;
var stopped=false;
var sessionToken=null;
var batchMode=false;   // loopback MessagePort boundary expects complete carrier batches

// The carrier nonce travels in the URL fragment and authenticates the
// injected WebView boundary; fragments are never sent in the HTTPS request.
var nonce=null;
var m=location.hash.match(/^#android=([A-Za-z0-9_-]+)/);
if(m)nonce=m[1];
history.replaceState(null,'','/');

function sleep(ms){return new Promise(function(r){setTimeout(r,ms);});}

// ---- client boundary -----------------------------------------------------
// Injected boundary: the app pre-defines window.TelegramWebProxy; binary
// frames arrive as {data: ArrayBuffer} via onmessage, controls are sent as
// JSON strings. Loopback boundary: the parent transfers a MessagePort; binary
// frames arrive as raw ArrayBuffers, controls as structured-clone objects.
var tp=window.TelegramWebProxy;
var port=null;
var injected=tp&&typeof tp.postMessage==='function';

if(injected){
  tp.onmessage=function(v){onClientValue(v&&typeof v==='object'&&v.data!==undefined?v.data:v);};
}else{  window.addEventListener('message',function(ev){
    if(ev.source!==window.parent)return;
    if(String(ev.origin).indexOf('http://127.0.0.1:')!==0)return;
    var d=ev.data;
    if(d&&d.t==='tproxy-init'&&d.v===1&&ev.ports&&ev.ports.length===1&&!port){
      port=ev.ports[0];
      batchMode=true;
      port.start();
      port.onmessage=function(e){onClientValue(e.data);};
    }
  });
}

function sendControl(obj){
  try{
    if(injected)tp.postMessage(JSON.stringify(obj));
    else if(port)port.postMessage(obj);
  }catch(e){}
}
// Send binary as a standalone ArrayBuffer copy.
function sendBytes(u8){
  try{
    var ab=u8.buffer.slice(u8.byteOffset,u8.byteOffset+u8.length);
    if(injected)tp.postMessage(ab);
    else if(port)port.postMessage(ab);
  }catch(e){}
}
function sendStatus(st){sendControl({t:'status',state:st});}
function sendCloseMsg(){sendControl({t:'close'});}
function wake(){if(queueWaiter){var w=queueWaiter;queueWaiter=null;w();}}

// ---- frame plumbing -------------------------------------------------------

function frameLen(u,off){return ((u[off+4]<<24)|(u[off+5]<<16)|(u[off+6]<<8)|u[off+7])>>>0;}
function frameSid(u,off){return ((u[off+1]<<16)|(u[off+2]<<8)|u[off+3])>>>0;}
function frameType(u,off){return u[off];}

function splitFrames(buf){
  var u=new Uint8Array(buf),out=[],off=0;
  while(off<u.length){
    var end=off+8+frameLen(u,off);
    out.push(u.slice(off,end));
    off=end;
  }
  return out;
}

// ---- lanes state (https-lanes / websocket-lanes) --------------------------

var lanes={};
function lane(sid){
  var L=lanes[sid];
  if(!L)L=lanes[sid]={q:[],bytes:0,waiter:null,seq:1,cursor:0,closed:false,sawClose:false,started:false,sentOpen:false};
  return L;
}
function pushLane(sid,buf){
  var L=lane(sid);
  if(L.bytes+buf.byteLength>PENDING_LIMIT)return;
  L.q.push(buf);L.bytes+=buf.byteLength;
  if(L.waiter){var w=L.waiter;L.waiter=null;w();}
}
function takeLaneBatch(sid){
  var L=lane(sid);
  return new Promise(function(resolve){
    function emit(){
      var items=[],bytes=0;
      while(L.q.length&&bytes<UP_LIMIT){var b=L.q.shift();items.push(b);bytes+=b.byteLength;}
      L.bytes-=bytes;
      var out=new Uint8Array(bytes),off=0;
      for(var i=0;i<items.length;i++){out.set(new Uint8Array(items[i]),off);off+=items[i].byteLength;}
      resolve(out);
    }
    if(L.q.length)emit();else L.waiter=emit;
  });
}
// Requeue a buffer at the head of a lane (OPEN-first splitting).
function unshiftLane(sid,u8){
  var L=lane(sid);
  L.q.unshift(u8.buffer.slice(u8.byteOffset,u8.byteOffset+u8.length));
  L.bytes+=u8.byteLength;
}
function deliverDown(body){
  if(batchMode){scanClose(body);sendBytes(body);return;}
  var fs=splitFrames(body);
  for(var i=0;i<fs.length;i++){
    if(frameType(fs[i],0)===3)lane(frameSid(fs[i],0)).sawClose=true;
    sendBytes(fs[i]);
  }
}
function scanClose(body){
  var u=new Uint8Array(body),off=0;
  while(off<u.length){
    if(frameType(u,off)===3)lane(frameSid(u,off)).sawClose=true;
    off+=8+frameLen(u,off);
  }
}

function onClientValue(v){
  if(v&&typeof v==='object'&&!(v instanceof ArrayBuffer)&&v.t==='close'){
    
    stopped=true;
    if(sessionToken){
      try{fetch('/api/v1/session',{method:'DELETE',mode:'same-origin',credentials:'omit',cache:'no-store',redirect:'error',headers:{'Authorization':'Bearer '+sessionToken},keepalive:true});}catch(e){}
    }
    return;
  }
  if(!(v instanceof ArrayBuffer))return;   // control values are not carrier data
  if(LANES){
    // Route each complete frame to the lane of its stream id; only the shared
    // header and frame boundary are inspected, never the payload.
    if(!started){
      started=true;
      firstBuffer=v;
      if(firstReady){var r=firstReady;firstReady=null;r(v);}
      return;
    }
    var fs=splitFrames(v);
    for(var i=0;i<fs.length;i++)pushLane(frameSid(fs[i],0),fs[i]);
    return;
  }
  if(started){
    if(pendingBytes+v.byteLength>PENDING_LIMIT)return;
    pending.push(v);pendingBytes+=v.byteLength;wake();
    return;
  }
  firstBuffer=v;started=true;
  
  if(firstReady){var r2=firstReady;firstReady=null;r2(v);}
}

function takeFirst(){
  if(firstBuffer)return Promise.resolve(firstBuffer);
  return new Promise(function(r){firstReady=r;});
}

function takeBatch(){
  return new Promise(function(resolve){
    function emit(){
      var items=[],bytes=0;
      while(pending.length&&bytes<UP_LIMIT){var b=pending.shift();items.push(b);bytes+=b.byteLength;}
      pendingBytes-=bytes;
      var out=new Uint8Array(bytes),off=0;
      for(var i=0;i<items.length;i++){out.set(new Uint8Array(items[i]),off);off+=items[i].byteLength;}
      resolve(out);
    }
    if(pending.length)emit();else queueWaiter=emit;
  });
}

function post(url,headers,body){
  return fetch(url,{method:'POST',mode:'same-origin',credentials:'omit',cache:'no-store',redirect:'error',referrerPolicy:'no-referrer',headers:headers,body:body});
}

// ---- carrier -------------------------------------------------------------

async function createSession(){
  var hello;
  if(LANES){
    // The create body is a single HELLO frame; later frames of the first
    // message are re-routed to their lanes.
    var buf=await takeFirst();
    var fs=splitFrames(buf);
    hello=fs[0];
    for(var i=1;i<fs.length;i++)pushLane(frameSid(fs[i],0),fs[i]);
  }else{
    hello=await takeFirst();
  }
  var r=await post('/api/v1/session',
    {'Authorization':'Bearer '+'{{BOOTSTRAP}}','Content-Type':'application/octet-stream'},hello);
  if(!r.ok)throw new Error('session create failed: '+r.status);
  sessionToken=r.headers.get('X-Session-Token');
  var mode=r.headers.get('X-Carrier-Mode')||MODE;
  var cursor=parseInt(r.headers.get('X-Down-Cursor')||'0',10);
  var welcome=new Uint8Array(await r.arrayBuffer());
  sendBytes(welcome);   // the app validates exactly one WELCOME frame
  return {token:sessionToken,cursor:cursor,mode:mode};
}

async function upLoop(tok){
  var seq=1;
  for(;;){
    if(stopped)return;
    var body=await takeBatch();
    for(;;){
      if(stopped)return;
      var r;
      try{
        r=await post('/api/v1/up',
          {'Authorization':'Bearer '+tok,'Content-Type':'application/octet-stream','X-Up-Seq':String(seq)},body);
      }catch(e){await sleep(500);continue;}
      if(r.status===204){seq++;break;}
      if(r.status===503){await sleep(parseInt(r.headers.get('Retry-After')||'1',10)*1000);continue;}
      throw new Error('uplink failed: '+r.status);
    }
  }
}

async function downLoop(tok,cursor0){
  var cursor=cursor0;
  for(;;){
    if(stopped)return;
    var r;
    try{
      r=await post('/api/v1/down',
        {'Authorization':'Bearer '+tok,'X-Down-Cursor':String(cursor)},new Uint8Array(0));
    }catch(e){await sleep(500);continue;}
    if(r.status===204)continue;
    if(r.status===200){
      var body=new Uint8Array(await r.arrayBuffer());
      cursor=parseInt(r.headers.get('X-Down-Cursor')||String(cursor),10);
      if(batchMode){
        sendBytes(body);            // loopback boundary: complete relay batch
      }else{
        var fs=splitFrames(body);   // injected boundary: individual frames
        for(var i=0;i<fs.length;i++)sendBytes(fs[i]);
      }
      continue;
    }
    throw new Error('downlink failed: '+r.status);
  }
}

// ---- https-lanes ----------------------------------------------------------

function laneUpLoop(tok,sid){
  var L=lane(sid);
  return (async function(){
    for(;;){
      if(stopped||L.closed)return;
      var body=await takeLaneBatch(sid);
      for(;;){
        if(stopped||L.closed)return;
        var r;
        try{
          r=await post('/api/v1/up',
            {'Authorization':'Bearer '+tok,'Content-Type':'application/octet-stream',
             'X-Up-Seq':String(L.seq),'X-Lane-ID':String(sid)},body);
        }catch(e){await sleep(500);continue;}
        if(r.status===204){L.seq++;break;}
        if(r.status===503){await sleep(parseInt(r.headers.get('Retry-After')||'1',10)*1000);continue;}
        throw new Error('lane uplink failed: '+r.status);
      }
    }
  })();
}

function laneDownLoop(tok,sid){
  var L=lane(sid);
  return (async function(){
    for(;;){
      if(stopped)return;
      if(sid>0&&L.closed)return;
      var r;
      try{
        r=await post('/api/v1/down',
          {'Authorization':'Bearer '+tok,'X-Down-Cursor':String(L.cursor),'X-Lane-ID':String(sid)},
          new Uint8Array(0));
      }catch(e){await sleep(500);continue;}
      if(r.status===204){
        if(r.headers.get('X-Lane-Closed')==='1'){L.closed=true;return;}
        continue;
      }
      if(r.status===200){
        var body=new Uint8Array(await r.arrayBuffer());
        L.cursor=parseInt(r.headers.get('X-Down-Cursor')||String(L.cursor),10);
        deliverDown(body);
        continue;
      }
      throw new Error('lane downlink failed: '+r.status);
    }
  })();
}

function startLaneLoops(tok,sid){
  var L=lane(sid);
  if(L.started)return;
  L.started=true;
  laneTasks.push(laneUpLoop(tok,sid));
  laneTasks.push(laneDownLoop(tok,sid));
}
var laneTasks=[];

async function httpsLanesLoop(tok){
  // lane 0 (session-level PONG traffic) runs immediately
  startLaneLoops(tok,0);
  for(;;){
    if(stopped)return;
    await new Promise(function(r){setTimeout(r,50);});
    for(var sid in lanes){
      if(sid!=='0'&&+sid>0&&!lanes[sid].started)startLaneLoops(tok,+sid);
    }
  }
}

// ---- websocket / websocket-lanes ------------------------------------------

async function wsLoop(tok){
  var ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/api/v1/ws','tproxy-v1.'+tok);
  ws.binaryType='arraybuffer';
  ws.onmessage=function(ev){
    if(typeof ev.data==='string')return;
    if(batchMode){
      sendBytes(new Uint8Array(ev.data));
    }else{
      var fs=splitFrames(ev.data);
      for(var i=0;i<fs.length;i++)sendBytes(fs[i]);
    }
  };
  ws.onclose=function(){stopped=true;};
  await new Promise(function(res,rej){ws.onopen=res;ws.onerror=rej;});
  for(;;){
    if(stopped||ws.readyState!==1)return;
    var b=await takeBatch();
    if(ws.readyState!==1)return;
    ws.send(b);
  }
}

function wsLane(tok,sid){
  var L=lane(sid);
  return (async function(){
    var ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/api/v1/ws',
      'tproxy-lane-v1.'+tok+'.'+String(sid));
    L.ws=ws;
    ws.binaryType='arraybuffer';
    ws.onmessage=function(ev){
      if(typeof ev.data==='string')return;
      deliverDown(new Uint8Array(ev.data));
    };
    ws.onclose=function(){
      L.closed=true;
      if(!L.sawClose){
        // Unexpected lane loss: the bridge reports the stream as closed.
        var c=new Uint8Array(8);
        c[0]=3;c[1]=(sid>>16)&255;c[2]=(sid>>8)&255;c[3]=sid&255;
        sendBytes(c);
      }
    };
    await new Promise(function(res,rej){ws.onopen=res;ws.onerror=rej;});
    for(;;){
      if(stopped||ws.readyState!==1||L.closed)return;
      var body=await takeLaneBatch(sid);
      if(!L.sentOpen){
        // The first WebSocket message on a lane is exactly one OPEN frame.
        var fs=splitFrames(body);
        if(fs.length>1){
          var restLen=body.byteLength-fs[0].byteLength;
          var rest=new Uint8Array(restLen),off=0;
          for(var i=1;i<fs.length;i++){rest.set(fs[i],off);off+=fs[i].byteLength;}
          unshiftLane(sid,rest);
          body=fs[0];
        }
        L.sentOpen=true;
        ws.send(body);
        continue;
      }
      if(ws.readyState!==1)return;
      ws.send(body);
    }
  })();
}

async function wsLanesLoop(tok){
  lane(0); // session-level state exists, but lane zero has no WebSocket
  for(;;){
    if(stopped)return;
    var started_any=false;
    for(var sid in lanes){
      var L=lanes[sid];
      if(+sid>0&&!L.started&&!L.closed){
        L.started=true;
        laneTasks.push(wsLane(tok,+sid));
        started_any=true;
      }
    }
    await new Promise(function(r){setTimeout(r,started_any?100:50);});
  }
}

async function start(){
  try{
    
    // Authenticate the injected carrier instance before anything else.
    if(injected&&nonce){sendControl({t:'tproxy-android-init',v:1,nonce:nonce});}
    sendStatus('connecting');
    var s=await createSession();
    
    sendStatus('connected');
    if(s.mode==='websocket'){
      await wsLoop(s.token);
    }else if(s.mode==='websocket-lanes'){
      await Promise.race([wsLanesLoop(s.token)].concat(laneTasks));
    }else if(s.mode==='https-lanes'){
      await Promise.race([httpsLanesLoop(s.token)].concat(laneTasks));
    }else{
      var tasks=[upLoop(s.token),downLoop(s.token,s.cursor)];
      await Promise.race(tasks);
    }
  }catch(e){
    
    sendStatus('failed');
  }
  sendCloseMsg();
}

start();
})();
</script>
</body>
</html>
""";

    public static async Task Write(HttpContext ctx, string bootstrap, string mode, string hostname)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var html = Template
            .Replace("{{NONCE}}", nonce)
            .Replace("{{BOOTSTRAP}}", bootstrap)
            .Replace("{{MODE}}", mode);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; base-uri 'none'; child-src 'none'; " +
            $"connect-src 'self' wss://{hostname}; " +
            "font-src 'none'; form-action 'none'; frame-ancestors http://127.0.0.1:*; frame-src 'none'; " +
            "img-src 'none'; manifest-src 'none'; media-src 'none'; object-src 'none'; " +
            $"script-src 'nonce-{nonce}'; style-src 'none'; worker-src 'none'; " +
            "sandbox allow-same-origin allow-scripts";
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        await ctx.Response.Body.WriteAsync(bytes);
    }
}
