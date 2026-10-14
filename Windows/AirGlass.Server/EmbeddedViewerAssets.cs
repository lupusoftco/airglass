namespace AirGlass.Server;

public static class EmbeddedViewerAssets
{
    public static string GetHtml()
    {
        return @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>AirGlass Viewer</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #000; color: #fff; }
        #container { width: 100vw; height: 100vh; display: flex; align-items: center; justify-content: center; }
        #video { max-width: 100%; max-height: 100%; }
        #status { position: fixed; top: 20px; left: 20px; padding: 10px 20px; background: rgba(0,0,0,0.7); border-radius: 8px; font-size: 14px; }
        .connected { color: #0f0; }
        .disconnected { color: #f00; }
    </style>
</head>
<body>
    <div id='status' class='disconnected'>Connecting...</div>
    <div id='container'>
        <video id='video' playsinline autoplay></video>
    </div>
    <script>
        const pc = new RTCPeerConnection({ iceServers: [{ urls: ['stun:stun.l.google.com:19302'] }] });
        const video = document.getElementById('video');
        const status = document.getElementById('status');

        pc.ontrack = (e) => { video.srcObject = e.streams[0]; };
        pc.onconnectionstatechange = () => {
            status.textContent = pc.connectionState === 'connected' ? '✓ Connected' : '✗ ' + pc.connectionState;
            status.className = pc.connectionState === 'connected' ? 'connected' : 'disconnected';
        };
        pc.onicecandidate = (e) => { if (e.candidate) fetch('/ice-candidate', { method: 'POST', body: JSON.stringify(e.candidate.toJSON()) }); };

        fetch('/offer').then(r => r.json()).then(async d => {
            await pc.setRemoteDescription(new RTCSessionDescription(JSON.parse(d.offer)));
            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);
            fetch('/answer', { method: 'POST', body: JSON.stringify(answer) });
        });
    </script>
</body>
</html>";
    }
}
