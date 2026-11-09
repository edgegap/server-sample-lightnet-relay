// Browser WebSocket bridge for RelayWebSocket in WebGL builds, where .NET's ClientWebSocket and threads don't exist.
// The C# side polls once per frame (RelayPeer.Poll), so nothing here calls back into C#: the browser's WebSocket
// queues what it receives and C# drains the queue with RelayWs_NextLength + RelayWs_Receive.
var RelayWebSocketBridge = {
    $RelayWs: { next: 1, sockets: {} },

    // returns a socket id (> 0)
    RelayWs_Open: function (urlPtr) {
        var id = RelayWs.next++;
        var s = { ws: null, state: 0, inbox: [], reason: "" };   // state: 0 connecting, 1 open, 2 closed
        RelayWs.sockets[id] = s;
        try {
            s.ws = new WebSocket(UTF8ToString(urlPtr));
        } catch (e) {
            s.state = 2;
            s.reason = "cannot connect to the relay: " + e.message;
            return id;
        }
        s.ws.binaryType = "arraybuffer";
        s.ws.onopen = function () { if (s.state === 0) s.state = 1; };
        s.ws.onmessage = function (e) {
            if (e.data instanceof ArrayBuffer) s.inbox.push(new Uint8Array(e.data));
        };
        s.ws.onerror = function () { };   // onclose follows with the details
        s.ws.onclose = function (e) {
            if (s.state === 2) return;
            s.reason = s.state === 1 ? "relay connection lost" : "cannot connect to the relay (WebSocket close " + e.code + ")";
            s.state = 2;
        };
        return id;
    },

    RelayWs_State: function (id) {
        var s = RelayWs.sockets[id];
        return s ? s.state : 2;
    },

    // the close reason as a UTF-8 string the C# side frees with _free (via Marshal.PtrToStringUTF8 + RelayWs_Free)
    RelayWs_Reason: function (id) {
        var s = RelayWs.sockets[id];
        var text = s ? s.reason : "relay connection lost";
        var size = lengthBytesUTF8(text) + 1;
        var ptr = _malloc(size);
        stringToUTF8(text, ptr, size);
        return ptr;
    },

    RelayWs_Free: function (ptr) {
        _free(ptr);
    },

    // returns 0 when sent, -1 when the socket isn't open
    RelayWs_Send: function (id, ptr, length) {
        var s = RelayWs.sockets[id];
        if (!s || s.state !== 1 || s.ws.readyState !== 1) return -1;
        // copy: the WASM heap can move (memory growth) and ws.send may hold on to the view
        s.ws.send(HEAPU8.slice(ptr, ptr + length));
        return 0;
    },

    // the next received frame's length, or -1 when there is none
    RelayWs_NextLength: function (id) {
        var s = RelayWs.sockets[id];
        return s && s.inbox.length > 0 ? s.inbox[0].length : -1;
    },

    // copies the next received frame into the buffer and removes it from the queue
    RelayWs_Receive: function (id, ptr, length) {
        var s = RelayWs.sockets[id];
        if (!s || s.inbox.length === 0) return -1;
        var frame = s.inbox.shift();
        var n = Math.min(frame.length, length);
        HEAPU8.set(frame.subarray(0, n), ptr);
        return n;
    },

    RelayWs_Close: function (id) {
        var s = RelayWs.sockets[id];
        if (!s) return;
        delete RelayWs.sockets[id];
        s.state = 2;
        try { if (s.ws) s.ws.close(1000); } catch (e) { }
    }
};

autoAddDeps(RelayWebSocketBridge, "$RelayWs");
mergeInto(LibraryManager.library, RelayWebSocketBridge);
