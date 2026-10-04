"use strict";

(() => {
  // URL fragment: "#<token>". It never reaches the server in an HTTP
  // request. Remove it from the address bar and history right away so it
  // cannot be copied or reopened.
  const token = decodeURIComponent(location.hash.slice(1).split("&")[0]);
  history.replaceState(null, "", location.pathname);

  // --- Diagnostics log ---------------------------------------------------
  //
  // Always collected; the panel starts hidden and shows everything since the
  // page loaded when opened with the button in the corner.

  const debugEl = document.getElementById("debug");
  const debugToggle = document.getElementById("debug-toggle");
  const maxLogLines = 1000;

  const log = (text) => {
    const line = `${new Date().toISOString().slice(11, 23)} ${text}`;
    console.log(line);
    const row = document.createElement("div");
    row.textContent = line;
    debugEl.appendChild(row);
    while (debugEl.childElementCount > maxLogLines) debugEl.firstChild.remove();
    if (!debugEl.hidden) debugEl.scrollTop = debugEl.scrollHeight;
  };

  debugToggle.addEventListener("click", (event) => {
    event.stopPropagation();
    debugEl.hidden = !debugEl.hidden;
    debugToggle.setAttribute("aria-pressed", String(!debugEl.hidden));
    if (!debugEl.hidden) debugEl.scrollTop = debugEl.scrollHeight;
  });
  // Scrolling or selecting in the log must not count as a tap on the video.
  debugEl.addEventListener("click", (event) => event.stopPropagation());

  window.addEventListener("error", (event) => {
    log(`JS HATA: ${event.message} (${(event.filename || "").split("/").pop()}:${event.lineno}:${event.colno})`);
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason;
    log(`YAKALANMAMIŞ PROMISE: ${reason && reason.name ? `${reason.name}: ${reason.message}` : reason}`);
  });

  /** "host 192.168.1.5:50000 udp" from an SDP candidate line. */
  const describeCandidate = (sdp) => {
    const fields = sdp.split(" ");
    const typ = fields.indexOf("typ");
    return `${typ >= 0 ? fields[typ + 1] : "?"} ${fields[4]}:${fields[5]} ${fields[2]}`;
  };

  const describe = (message) =>
    `${message.type}${message.type === "candidate" ? ` ${describeCandidate(message.candidate || "")}` : ""}`;

  log(`UA: ${navigator.userAgent}`);
  log(`secureContext=${window.isSecureContext} RTCPeerConnection=${typeof window.RTCPeerConnection} wakeLock=${"wakeLock" in navigator}`);

  // --- Screens -------------------------------------------------------------
  //
  // 1 · connecting (spinner), 2 · live (video only), 3 · ended (message).

  const video = document.getElementById("video");
  const connectingEl = document.getElementById("connecting");
  const endedEl = document.getElementById("ended");
  const endedTitle = document.getElementById("ended-title");
  const endedMessage = document.getElementById("ended-message");

  const ENDINGS = {
    disconnected: ["Bağlantı kesildi", "Yeniden izlemek için Mac'teki QR kodu yeniden okutun."],
    rejected: ["Bağlanılamadı", "Bu QR kod artık geçerli değil. Mac'teki güncel QR kodu okutun."],
    noToken: ["Bağlantı geçersiz", "Mac'teki QR kodu telefonunun kamerasıyla okutun."],
    unsupported: ["Görüntü açılamadı", "Bu tarayıcı görüntüyü oynatamıyor."],
  };

  const showLive = () => {
    connectingEl.hidden = true;
    endedEl.hidden = true;
  };

  const showEnded = (kind) => {
    const [title, message] = ENDINGS[kind];
    endedTitle.textContent = title;
    endedMessage.textContent = message;
    connectingEl.hidden = true;
    endedEl.hidden = false;
  };

  // --- Keeping the screen awake -------------------------------------------
  //
  // 1. Screen Wake Lock API, if the browser offers it (it needs a secure
  //    context, so over plain http it is usually missing).
  // 2. Otherwise a tiny silent video with a (silent) audio track, started by
  //    the first tap, the technique NoSleep.js uses. WebKit lets looping
  //    media sleep, so it is rewound by hand instead of using `loop`.
  // Native fullscreen video keeps the screen on by itself as well.

  const keepAwake = (() => {
    let method = null;
    let wakeLock = null;
    let silentVideo = null;

    const requestWakeLock = async () => {
      try {
        wakeLock = await navigator.wakeLock.request("screen");
        wakeLock.addEventListener("release", () => log("Wake Lock bırakıldı"));
        return true;
      } catch (error) {
        log(`Wake Lock alınamadı: ${error.name}: ${error.message}`);
        return false;
      }
    };

    // Must run inside a user gesture: it starts playback with sound.
    const playSilentVideo = () => {
      if (!silentVideo) {
        silentVideo = document.createElement("video");
        silentVideo.className = "keepawake";
        silentVideo.setAttribute("playsinline", "");
        silentVideo.setAttribute("aria-hidden", "true");
        silentVideo.preload = "auto";
        silentVideo.src = "keepawake.mp4";
        silentVideo.addEventListener("timeupdate", () => {
          if (silentVideo.currentTime > 1.5) silentVideo.currentTime = 0.5;
        });
        silentVideo.addEventListener("ended", () => silentVideo.play().catch(() => {}));
        document.body.appendChild(silentVideo);
      }
      return silentVideo.play().then(
        () => true,
        (error) => {
          log(`Sessiz video oynatılamadı: ${error.name}: ${error.message}`);
          return false;
        },
      );
    };

    return {
      /** Without a gesture: only the Wake Lock API can start here. */
      async startIfPossible() {
        if (method || !("wakeLock" in navigator)) return;
        if (await requestWakeLock()) {
          method = "wakeLock";
          log("Ekran açık tutma: Screen Wake Lock API");
        }
      },
      /** From a tap. */
      async start() {
        if (method) return;
        if (!("wakeLock" in navigator)) {
          // Started synchronously so the tap still counts as the gesture.
          const started = playSilentVideo();
          if (await started) {
            method = "video";
            log("Ekran açık tutma: sessiz döngü video (NoSleep yöntemi; Wake Lock API yok)");
          }
          return;
        }
        await this.startIfPossible();
      },
      async resume() {
        if (method === "wakeLock" && (!wakeLock || wakeLock.released)) await requestWakeLock();
        if (method === "video") silentVideo.play().catch(() => {});
      },
      stop() {
        if (!method) return;
        if (wakeLock) wakeLock.release().catch(() => {});
        if (silentVideo) silentVideo.pause();
        wakeLock = null;
        method = null;
        log("Ekran açık tutma kapatıldı");
      },
      get method() {
        return method;
      },
    };
  })();

  if ("wakeLock" in navigator) {
    keepAwake.startIfPossible();
  } else {
    log("Screen Wake Lock API yok; ilk dokunuşta sessiz video başlayacak");
  }

  document.addEventListener("visibilitychange", () => {
    log(`visibility → ${document.visibilityState}`);
    if (document.visibilityState === "visible") keepAwake.resume();
  });

  if (!token) {
    log("Fragment'ta token yok");
    showEnded("noToken");
    return;
  }

  // --- Signaling over plain HTTP -----------------------------------------
  //
  // POST /signal/hello {token} → {session}
  // POST /signal/send  {message}           (our messages, strictly in order)
  // GET  /signal/poll  → [messages]        (long poll for the Mac's messages)
  // POST /signal/bye                       (page closed)

  let session = null;
  let peer = null;
  let ended = false;

  const request = (method, path, body, options = {}) =>
    fetch(path, {
      method,
      cache: "no-store",
      headers: {
        ...(body ? { "Content-Type": "application/json" } : {}),
        ...(session ? { "X-AirGlass-Session": session } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      ...options,
    });

  const end = (kind) => {
    if (ended) return;
    ended = true;
    log(`Bitti: ${kind}`);
    if (peer) peer.close();
    exitFullscreen();
    keepAwake.stop();
    fullscreenButton.hidden = true;
    video.srcObject = null;
    showEnded(kind);
  };

  // Sends are chained so the Mac receives them in order (an answer must
  // arrive before the candidates that follow it).
  let sendChain = Promise.resolve();
  const send = (message) => {
    sendChain = sendChain.then(async () => {
      if (ended) return;
      log(`→ ${describe(message)}`);
      const response = await request("POST", "/signal/send", message);
      if (!response.ok) {
        log(`→ ${message.type} reddedildi: HTTP ${response.status}`);
        end("disconnected");
      }
    }).catch((error) => {
      log(`→ ${message.type} gönderilemedi: ${error.name}: ${error.message}`);
      end("disconnected");
    });
  };

  const poll = async () => {
    let failures = 0;
    while (!ended) {
      let response;
      try {
        response = await request("GET", "/signal/poll");
      } catch (error) {
        // A dropped long poll (e.g. while the phone was briefly asleep) is
        // not fatal; give up only after several in a row.
        failures += 1;
        log(`poll ağ hatası (${failures}): ${error.name}: ${error.message}`);
        if (failures >= 5) return end("disconnected");
        await new Promise((resolve) => setTimeout(resolve, 500 * failures));
        continue;
      }
      failures = 0;
      if (!response.ok) {
        log(`poll: HTTP ${response.status}`);
        return end("disconnected");
      }
      const messages = await response.json();
      for (const message of messages) {
        log(`← ${describe(message)}`);
        enqueue(message);
      }
    }
  };

  // --- Fullscreen ----------------------------------------------------------
  //
  // iPhone Safari only allows real fullscreen for a <video> element, and only
  // from a user gesture (webkitEnterFullscreen). The first tap goes
  // fullscreen; afterwards a tap shows (or hides) the fullscreen button,
  // which hides itself again after 3 seconds.

  const fullscreenButton = document.getElementById("fullscreen");
  let firstTapHandled = false;
  let hideButtonTimer = null;

  const isLive = () => !ended && Boolean(video.srcObject) && video.readyState >= 1;
  const isFullscreen = () => Boolean(video.webkitDisplayingFullscreen || document.fullscreenElement);

  const enterFullscreen = () => {
    if (!isLive() || isFullscreen()) return;
    try {
      if (video.webkitEnterFullscreen) {
        video.webkitEnterFullscreen();
      } else if (document.documentElement.requestFullscreen) {
        document.documentElement.requestFullscreen().catch((error) => log(`Tam ekran reddedildi: ${error.name}`));
      } else {
        log("Tam ekran bu tarayıcıda yok");
      }
    } catch (error) {
      log(`Tam ekran açılamadı: ${error.name}: ${error.message}`);
    }
  };

  function exitFullscreen() {
    try {
      if (video.webkitDisplayingFullscreen && video.webkitExitFullscreen) video.webkitExitFullscreen();
      else if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    } catch {
      // Already out of fullscreen.
    }
  }

  const hideFullscreenButton = () => {
    clearTimeout(hideButtonTimer);
    fullscreenButton.hidden = true;
  };

  const toggleFullscreenButton = () => {
    if (!fullscreenButton.hidden) return hideFullscreenButton();
    if (!isLive() || isFullscreen()) return;
    fullscreenButton.hidden = false;
    clearTimeout(hideButtonTimer);
    hideButtonTimer = setTimeout(hideFullscreenButton, 3000);
  };

  document.addEventListener("click", () => {
    keepAwake.start();
    if (!firstTapHandled && isLive()) {
      firstTapHandled = true;
      enterFullscreen();
    } else {
      toggleFullscreenButton();
    }
  });

  fullscreenButton.addEventListener("click", (event) => {
    event.stopPropagation();
    hideFullscreenButton();
    enterFullscreen();
  });

  video.addEventListener("webkitbeginfullscreen", () => log("Tam ekran açıldı (iOS ekranı açık tutar)"));
  video.addEventListener("webkitendfullscreen", () => {
    log("Tam ekrandan çıkıldı");
    // iOS pauses the video when leaving its fullscreen player.
    if (!ended) video.play().catch(() => {});
  });
  // A live mirror never stays paused (e.g. paused from the fullscreen player).
  video.addEventListener("pause", () => {
    if (!ended && video.srcObject) video.play().catch(() => {});
  });
  window.addEventListener("resize", () => log(`Ekran ${innerWidth}×${innerHeight}`));

  // --- WebRTC --------------------------------------------------------------

  // If the connection drops, the Mac tries one ICE restart; give it this
  // long before calling it over.
  const recoveryTimeout = 15000;
  let recoveryTimer = null;

  const createPeer = () => {
    // Local network only: no STUN/TURN servers, host candidates only.
    const pc = new RTCPeerConnection({ iceServers: [] });

    pc.addEventListener("track", (event) => {
      log(`track: ${event.track.kind}`);
      // Low latency: play frames as soon as they are decodable.
      try {
        if ("jitterBufferTarget" in event.receiver) event.receiver.jitterBufferTarget = 0;
        if ("playoutDelayHint" in event.receiver) event.receiver.playoutDelayHint = 0;
      } catch (error) {
        log(`Gecikme ayarı uygulanamadı: ${error.name}`);
      }
      video.srcObject = event.streams[0] || new MediaStream([event.track]);
      video.play().catch((error) => log(`video.play() reddedildi: ${error.name}`));
    });
    pc.addEventListener("icecandidate", (event) => {
      if (!event.candidate || !event.candidate.candidate) {
        log("ICE toplama bitti");
        return;
      }
      send({
        type: "candidate",
        candidate: event.candidate.candidate,
        sdpMid: event.candidate.sdpMid,
        sdpMLineIndex: event.candidate.sdpMLineIndex,
      });
    });
    pc.addEventListener("icecandidateerror", (event) => {
      log(`ICE aday hatası: ${event.errorCode} ${event.errorText} ${event.address || ""}:${event.port || ""}`);
    });
    pc.addEventListener("signalingstatechange", () => log(`signalingState → ${pc.signalingState}`));
    pc.addEventListener("iceconnectionstatechange", () => log(`iceConnectionState → ${pc.iceConnectionState}`));
    pc.addEventListener("icegatheringstatechange", () => log(`iceGatheringState → ${pc.iceGatheringState}`));
    pc.addEventListener("connectionstatechange", () => {
      const state = pc.connectionState;
      log(`connectionState → ${state}`);
      if (state === "connected") {
        clearTimeout(recoveryTimer);
        recoveryTimer = null;
      } else if ((state === "disconnected" || state === "failed") && !recoveryTimer) {
        // Keep the last frame on screen while the Mac tries to recover.
        recoveryTimer = setTimeout(() => {
          if (pc.connectionState !== "connected") end("disconnected");
        }, recoveryTimeout);
      }
    });
    return pc;
  };

  video.addEventListener("playing", () => {
    log(`video oynuyor ${video.videoWidth}×${video.videoHeight}`);
    showLive();
  });
  video.addEventListener("resize", () => log(`video boyutu ${video.videoWidth}×${video.videoHeight}`));

  const handle = async (message) => {
    switch (message.type) {
      case "offer":
        // The first offer, or a renegotiation (ICE restart) on the same peer.
        if (!peer) peer = createPeer();
        await peer.setRemoteDescription({ type: "offer", sdp: message.sdp });
        await peer.setLocalDescription(await peer.createAnswer());
        send({ type: "answer", sdp: peer.localDescription.sdp });
        break;
      case "candidate":
        if (!peer) return;
        await peer.addIceCandidate({
          candidate: message.candidate,
          sdpMid: message.sdpMid || null,
          sdpMLineIndex: message.sdpMLineIndex,
        });
        break;
    }
  };

  // Handle the Mac's messages strictly in order: a candidate must not be
  // applied before the offer it belongs to.
  let handleChain = Promise.resolve();
  const enqueue = (message) => {
    handleChain = handleChain
      .then(() => handle(message))
      .catch((error) => {
        log(`${message.type} işlenemedi: ${error && error.name}: ${error && error.message}`);
        if (message.type === "offer" && !video.srcObject) end("unsupported");
      });
  };

  // Tell the Mac right away when the page goes away; keepalive lets the
  // request outlive the page.
  window.addEventListener("pagehide", () => {
    if (!session || ended) return;
    request("POST", "/signal/bye", null, { keepalive: true }).catch(() => {});
  });

  // --- Start ---------------------------------------------------------------

  (async () => {
    log("→ hello");
    let response;
    try {
      response = await request("POST", "/signal/hello", { token });
    } catch (error) {
      log(`hello ağ hatası: ${error.name}: ${error.message}`);
      return end("rejected");
    }
    if (!response.ok) {
      log(`hello reddedildi: HTTP ${response.status}`);
      return end("rejected");
    }
    session = (await response.json()).session;
    log("← hello kabul edildi");
    poll();
  })();
})();
