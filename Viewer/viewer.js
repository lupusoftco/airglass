"use strict";

(() => {
  const statusEl = document.getElementById("status");

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
  let welcomed = false;

  socket.addEventListener("open", () => {
    socket.send(JSON.stringify({ type: "hello", token }));
  });

  socket.addEventListener("message", (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }
    if (message.type === "welcome") {
      welcomed = true;
      setStatus("Bağlandı. Görüntü bekleniyor…");
    }
  });

  socket.addEventListener("close", () => {
    setStatus(welcomed ? "Bağlantı kesildi" : "Bağlanılamadı. QR kodu yeniden okutun.");
  });
})();
