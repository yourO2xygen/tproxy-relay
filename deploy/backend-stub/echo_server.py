"""Fake MTProxy backend: echoes every connection byte-for-byte.

Proves the full chain: client -> nginx -> relay -> backend -> relay -> nginx -> client.
Replace with the real MTProxy container for hosted testing.
"""
import socketserver
import sys
import threading


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        peer = self.client_address
        print(f"backend: connection from {peer}", flush=True)
        first = True
        while True:
            try:
                data = self.request.recv(65536)
            except ConnectionError:
                break
            if not data:
                break
            if first:
                print(f"backend: first bytes from {peer}: {data[:16].hex()}...", flush=True)
                first = False
            try:
                self.request.sendall(data)
            except ConnectionError:
                break
        print(f"backend: connection closed {peer}", flush=True)


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 9000
    print(f"backend-stub listening on 0.0.0.0:{port}", flush=True)
    Server(("0.0.0.0", port), Handler).serve_forever()
