# Протокол релея (tproxy-relay)

Нормативное описание транспортного протокола между клиентом Telegram Desktop
(веб-мост) и релеем. Референс — tproxy-server; поведение зафиксировано
поведенческими тестами (`tests/TproxyRelay.Tests`) и upstream-векторами.

## 1. Креды и capability

- Секрет MTProxy: 16 (или 17 с dd-префиксом) байт, hex в `TPROXY_SECRET_HEX`.
- **Capability** (то, что видно в URL моста) выводится из секрета:

  ```
  context    = UTF-8("tdesktop-web-proxy-bridge-v1\n" + hostname)                  (без base path)
             = UTF-8("tdesktop-web-proxy-bridge-v2\n" + hostname + "\n" + path)    (с base path)
  capability = base64url-no-padding(HMAC-SHA256(key=secret, message=context))      // 43 символа
  ```

- Управляющие токены (bootstrap/session): `16 случайных байт || 16 байт
  усечённого HMAC-SHA256`, base64url без паддинга — 43 символа, ключ
  HMAC — секрет из `token.key`.
- Сравнение capability и Bearer — constant-time.

## 2. HTTP API

Все пути — под корнем либо под `/<base>/` (см. [BASE_PATH.md](BASE_PATH.md)).

### 2.1 GET /?bridge=<capability>

Мост выдаёт bootstrap-страницу (HTML c JS-мостом и bootstrap-токеном).
Идемпотентность: повторный GET с тем же телом HELLO возвращает тот же токен
(дедупликация по hash тела, TTL `bootstrap_ttl_seconds`).

### 2.2 POST /api/v1/session

- `Authorization: Bearer <bootstrap>`; тело — один кадр HELLO (≤64 Б).
- 200: `X-Session-Token`, `X-Down-Cursor: 0`, `X-Carrier-Mode` (режим сессии),
  тело — кадр WELCOME.
- 400 `invalid_hello` / `redeem_invalid` / `body_too_large`; 503 + Retry-After
  при исчерпании ёмкости; прочие креды → публичная страница (без оракула).

### 2.3 POST /api/v1/up

- `Authorization: Bearer <session>`, `X-Up-Seq: <n>` (строго LastSeq+1),
  тело — пачка кадров (≤2 МиБ; сверх — 413 `body_too_large`, chunked
  обрезается потоково).
- 204 + `X-Up-Ack: <seq>`; byte-identical повтор seq → `DuplicateAcked`
  (тот же ack); тот же seq с другим телом → Fatal.
- 400 `bad_seq` / `bad_lane_header`; 409 `uplink:<причина>` — сессия закрыта;
  503 + Retry-After при переполнении бюджета (батч не применён частично).

### 2.4 POST /api/v1/down

- `Authorization: Bearer <session>`, `X-Down-Cursor: <n>`.
- long-poll до `long_poll_seconds`; новые поллы отменяют старые
  (newest-poll-wins).
- 200: батч кадров + `X-Down-Cursor` нового PendingCursor. Заряд батча
  держится, пока клиент не подтвердит курсор: следующий /down с
  `X-Down-Cursor == PendingCursor` — это ack (возвращает 204 или новый батч);
  с `X-Down-Cursor == AckedCursor` — byte-for-byte replay неподтверждённого
  батча. Любой другой курсор → 409 `protocol_error`, сессия закрыта.
- 204: пусто (+ `X-Lane-Closed: 1` в lanes-режиме при завершении лейна).

### 2.5 GET /api/v1/ws (websocket / websocket-lanes)

- Субпротокол `tproxy-v1.<session>` — мультиплексированная несущая: один
  сокет, downlink — бинарные пачки кадров (доставка = подтверждение,
  заряд снимается сразу), uplink — те же кадры, что и в /up.
- Субпротокол `tproxy-lane-v1.<session>.<sid>` — lanes-несущая: один сокет
  на стрим; **первое сообщение обязано быть одиночным кадром OPEN**, далее
  DATA/WINDOW/CLOSE этого лейна; text → закрытие; сообщение >2 МиБ → закрытие;
  чужой/битый субпротокол → публичный fallback (404).

## 3. Кадры

Заголовок 8 байт big-endian: `type(1) | stream_id(3) | payload_len(4)`.
Максимум payload — 1 МиБ (`max_frame_payload`), максимум кадров в пачке —
4096, stream_id ≤ 0xFFFFFF.

| Тип | Код | Направление | Смысл |
|---|---|---|---|
| OPEN | 0x01 | C→S | открыть стрим (id ≠ 0, без повторов; повтор → CLOSE) |
| DATA | 0x02 | обе | байты нагрузки |
| CLOSE | 0x03 | обе | закрыть стрим (id может переиспользоваться после tombstone) |
| WINDOW | 0x04 | C→S | выдать кредит `SendAvail` (начальный — 4 МиБ на стрим) |
| PING/PONG | 0x05/0x06 | обе | keepalive (lane 0 в lanes-режимах несёт только PONG) |
| HELLO/WELCOME | 0x10/0x11 | C↔S | создание сессии |
| BYE | 0x1F | обе | вежливое закрытие |

Flow control: релей читает из бэкенда только в пределах `SendAvail`; кредит
клиенту (`RecvAvail`, начально 4 МиБ) возвращается коалесцированными
WINDOW-кадрами на границах батчей.

## 4. Бюджеты и отказы

- Сессия: pending ≤ `max_pending_per_session` (+ одна пачка), стримов ≤
  `max_streams_per_session`; глобально: сессии/стримы/pending-байты/
  pending-элементы/dials-in-flight.
- Переполнение downlink-бюджета → 503-подобное закрытие сессии (кадры
  дренируются, глобальные счётчики возвращаются к базовым).
- Закрытие сессии (любой путь, включая обрыв клиента): все заряды и pooled-
  буферы возвращаются — см. тесты `SessionBudgetTests`.

## 5. Коды ошибок (X-Error)

`cookie_rejected`, `bad_xff`, `body_too_large`, `bad_seq`, `bad_cursor`,
`bad_lane_header`, `invalid_hello`, `redeem_invalid`, `protocol_error`,
`uplink:<причина>` (усечена до 64 символов).
