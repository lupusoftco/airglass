"use strict";

(() => {
  const statusEl = document.getElementById("status");
  const video = document.getElementById("video");

  const setStatus = (text) => {
    statusEl.textContent = text || "";
    statusEl.hidden = !text;
  };

  // The token travels in the URL fragment, so it never reaches the server
  // as part of an HTTP request. Remove it from the address bar and history
  // right away so it cannot be copied or reopened.
  const token = decodeURIComponent(location.hash.slice(1));
  history.replaceState(null, "", location.pathname);

  if (!token) {
    setStatus("Bağlantı geçersiz. QR kodu yeniden okutun.");
    return;
  }

  const socket = new WebSocket(`ws://${location.host}/ws`);
  const send = (message) => {
    if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
  };

  // Local network only: no STUN/TURN servers, host candidates only.
  const peer = new RTCPeerConnection({ iceServers: [] });
  let welcomed = false;
  let ended = false;

  const end = (text) => {
    if (ended) return;
    ended = true;
    peer.close();
    socket.close();
    video.srcObject = null;
    setStatus(text);
  };

  peer.addEventListener("track", (event) => {
    video.srcObject = event.streams[0] || new MediaStream([event.track]);
    video.play().catch(() => {});
  });

  peer.addEventListener("icecandidate", (event) => {
    if (!event.candidate || !event.candidate.candidate) return;
    send({
      type: "candidate",
      candidate: event.candidate.candidate,
      sdpMid: event.candidate.sdpMid,
      sdpMLineIndex: event.candidate.sdpMLineIndex,
    });
  });

  peer.addEventListener("connectionstatechange", () => {
    if (peer.connectionState === "failed") end("Bağlantı kesildi");
  });

  video.addEventListener("playing", () => setStatus(""));

  const handle = async (message) => {
    switch (message.type) {
      case "welcome":
        welcomed = true;
        setStatus("Bağlandı. Görüntü bekleniyor…");
        break;
      case "offer":
        await peer.setRemoteDescription({ type: "offer", sdp: message.sdp });
        await peer.setLocalDescription(await peer.createAnswer());
        send({ type: "answer", sdp: peer.localDescription.sdp });
        break;
      case "candidate":
        await peer.addIceCandidate({
          candidate: message.candidate,
          sdpMid: message.sdpMid || null,
          sdpMLineIndex: message.sdpMLineIndex,
        });
        break;
    }
  };

  socket.addEventListener("open", () => {
    send({ type: "hello", token });
  });

  // Handle messages strictly in order: a candidate must not be applied
  // before the offer it belongs to.
  let queue = Promise.resolve();
  socket.addEventListener("message", (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }
    queue = queue.then(() => handle(message)).catch(() => {});
  });

  socket.addEventListener("close", () => {
    end(welcomed ? "Bağlantı kesildi" : "Bağlanılamadı. QR kodu yeniden okutun.");
  });
})();
