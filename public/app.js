// ─────────────────────────────────────────────────────────────────────────────
//  Airo Stream — WebRTC client  (served directly from C# HTTP server)
//  Target: 1080p @ 60fps, ≤ 25 Mbps on local network
// ─────────────────────────────────────────────────────────────────────────────
'use strict';

const ipInput         = document.getElementById('ipInput');
const connectBtn      = document.getElementById('connectBtn');
const fullscreenBtn   = document.getElementById('fullscreenBtn');
const menuBtn         = document.getElementById('menuBtn');
const statusIndicator = document.getElementById('statusIndicator');
const statsPanel      = document.getElementById('statsPanel');
const fpsCount        = document.getElementById('fpsCount');
const bwCount         = document.getElementById('bwCount');
const streamVideo     = document.getElementById('streamVideo');
const uiOverlay       = document.getElementById('uiOverlay');
const statsToggleBtn  = document.getElementById('statsToggleBtn');
const floatingStats   = document.getElementById('floatingStats');
const floatingFps     = document.getElementById('floatingFps');
const floatingBw      = document.getElementById('floatingBw');

// ─── State ───────────────────────────────────────────────────────────────────
let ws             = null;
let pc             = null;
let isConnected    = false;
let reconnectCount = 0;
// Maximum number of reconnect attempts before giving up
const MAX_RECONNECTS = 3;

// ─── Browser Support Check ───────────────────────────────────────────────────
if (!window.RTCPeerConnection || !window.WebSocket) {
    statusIndicator.className = 'status disconnected';
    statusIndicator.innerText = 'Browser Unsupported';
    connectBtn.disabled = true;
    console.error('Browser does not support RTCPeerConnection or WebSocket.');
}

// Auto-fill IP from the hostname the page was loaded from (QR code workflow)
const autoIp = window.location.hostname;
if (autoIp && autoIp !== 'localhost') {
    ipInput.value = autoIp;
}

// ─── UI auto-hide ─────────────────────────────────────────────────────────────
let controlTimeout;
const showControls = () => {
    menuBtn.classList.remove('hidden');
    fullscreenBtn.classList.remove('hidden');
    clearTimeout(controlTimeout);
    // Hide UI controls after 4000ms (4 seconds) of inactivity
    controlTimeout = setTimeout(() => {
        menuBtn.classList.add('hidden');
        fullscreenBtn.classList.add('hidden');
        uiOverlay.classList.add('hidden');
    }, 4000);
};

document.addEventListener('click',      showControls);
document.addEventListener('touchstart', showControls, { passive: true });
document.addEventListener('mousemove',  showControls);

menuBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    uiOverlay.classList.toggle('hidden');
    showControls();
});

for (const el of [uiOverlay, menuBtn, fullscreenBtn]) {
    el.addEventListener('click',      (e) => { e.stopPropagation(); showControls(); });
    el.addEventListener('touchstart', (e) => { e.stopPropagation(); showControls(); }, { passive: true });
    el.addEventListener('mouseenter', () => clearTimeout(controlTimeout));
    el.addEventListener('mouseleave', () => showControls());
}

// ─── Stats polling ────────────────────────────────────────────────────────────
// Recursive setTimeout ensures we never overlap getStats() calls
let lastBytesReceived = 0;
let lastFramesDecoded = 0;

function scheduleStatsUpdate() {
    setTimeout(async () => {
        if (isConnected && pc) {
            try {
                const stats = await pc.getStats();
                stats.forEach(report => {
                    if (report.type === 'inbound-rtp' && report.kind === 'video') {
                        const bytesNow  = report.bytesReceived || 0;
                        const framesNow = report.framesDecoded || 0;

                        if (lastBytesReceived !== 0) {
                            const bytesDiff  = bytesNow  - lastBytesReceived;
                            const framesDiff = framesNow - lastFramesDecoded;
                            const mbDisplay  = (bytesDiff / 1_048_576).toFixed(2) + ' MB/s';

                            fpsCount.innerText    = framesDiff;
                            bwCount.innerText     = mbDisplay;
                            floatingFps.innerText = `${framesDiff} FPS`;
                            floatingBw.innerText  = mbDisplay;
                        }
                        lastBytesReceived = bytesNow;
                        lastFramesDecoded = framesNow;
                    }
                });
            } catch (_) { /* peer connection closed */ }
        }
        scheduleStatsUpdate();
    }, 1000);
}
scheduleStatsUpdate();

statsToggleBtn.addEventListener('change', (e) => {
    if (e.target.checked && isConnected) {
        floatingStats.classList.remove('hidden');
    } else {
        floatingStats.classList.add('hidden');
    }
});

// ─── Connect / Disconnect ─────────────────────────────────────────────────────
connectBtn.addEventListener('click', () => {
    if (isConnected) { disconnect(); return; }
    const ip = ipInput.value.trim();
    if (!ip) return;
    reconnectCount = 0;
    connectToServer(ip);
});

async function connectToServer(ip) {
    setStatus('connecting', 'Connecting…');
    connectBtn.disabled = true;

    // 1. Get PIN from URL (avoiding literal "null" string if missing)
    const urlParams = new URLSearchParams(window.location.search);
    const pin = urlParams.get('pin') || '';
    const pinQuery = pin ? `?pin=${encodeURIComponent(pin)}` : '';
    
    // 2. Fetch ICE server configuration
    let iceServers = [{ urls: 'stun:stun.l.google.com:19302' }];
    try {
        const response = await fetch(`/config.json${pinQuery}`);
        if (response.ok) {
            const config = await response.json();
            if (config && config.iceServers && config.iceServers.length > 0) {
                iceServers = config.iceServers;
            }
        }
    } catch (e) { console.warn('Could not load config.json, falling back to default STUN', e); }

    // 3. Connect WebSocket with Auth PIN (dynamically switch between ws:// and wss://)
    // SECURITY NOTE: Passing the PIN as a query parameter means it may appear in reverse-proxy access logs.
    // For highly exposed public servers, migrating this to an in-band WebSocket auth message is recommended.
    // ORIGIN NOTE: There is no strict origin validation here. The PIN is the sole auth boundary, designed for trusted LANs.
    const wsScheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${wsScheme}://${window.location.host}/webrtc${pinQuery}`);

    ws.onopen = () => {
        pc = new RTCPeerConnection({
            iceServers: iceServers,
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require'
        });

        pc.ontrack = (event) => {
            streamVideo.srcObject     = event.streams[0];
            isConnected               = true;
            reconnectCount            = 0;
            setStatus('connected', 'Connected — 1080p 60fps');
            connectBtn.innerText      = 'Disconnect';
            connectBtn.disabled       = false;
            statsPanel.style.display  = 'flex';
            if (statsToggleBtn.checked) floatingStats.classList.remove('hidden');
            uiOverlay.classList.add('hidden');
            showControls();
        };

        pc.onicecandidate = (event) => {
            if (event.candidate && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'candidate', candidate: event.candidate }));
            }
        };

        pc.onconnectionstatechange = () => {
            console.log('[WebRTC] State:', pc.connectionState);
        };

        // Handle signaling messages from server
        ws.onmessage = async (event) => {
            try {
                const msg = JSON.parse(event.data);
                if (msg.type === 'answer') {
                    await pc.setRemoteDescription(
                        new RTCSessionDescription({ type: 'answer', sdp: msg.sdp }));
                } else if (msg.type === 'candidate') {
                    await pc.addIceCandidate(new RTCIceCandidate(msg.candidate));
                }
            } catch (err) {
                console.error('[Signaling]', err);
            }
        };

        // Modern transceiver API — more reliable than offerToReceiveVideo flag
        const transceiver = pc.addTransceiver('video', { direction: 'recvonly' });

        // Prefer H.264 High Profile for hardware decoding (critical for 60fps on iPad)
        applyCodecPreferences(transceiver);

        pc.createOffer()
            .then(offer => {
                // Add bandwidth hint to our offer SDP
                const mungedSdp = mutateSdpBandwidth(offer.sdp, 20480);
                const modOffer  = new RTCSessionDescription({ type: 'offer', sdp: mungedSdp });
                return pc.setLocalDescription(modOffer).then(() => modOffer);
            })
            .then(offer => {
                ws.send(JSON.stringify({ type: 'offer', sdp: offer.sdp }));
            })
            .catch(err => {
                console.error('[WebRTC] Offer error:', err);
                ws.close();
            });
    };

    ws.onclose = () => {
        const wasConnected = isConnected;
        cleanupConnection();

        // Allow retry even if it failed on the first connection attempt
        if (reconnectCount < MAX_RECONNECTS) {
            // Exponential backoff: 2s → 4s → 8s
            reconnectCount++;
            const delay = Math.pow(2, reconnectCount) * 1000;
            console.log(`[WebRTC] Disconnected. Retry ${reconnectCount}/${MAX_RECONNECTS} in ${delay / 1000}s`);
            setStatus('connecting', `Reconnecting… (${reconnectCount}/${MAX_RECONNECTS})`);
            setTimeout(() => connectToServer(ipInput.value.trim()), delay);
        } else {
            // Show meaningful error if we failed without ever connecting
            const reason = wasConnected ? 'Disconnected' : 'Connection Failed (See Console)';
            showDisconnectedUI(reason);
        }
    };

    ws.onerror = (err) => {
        console.error('[WebSocket] Error:', err);
        // We do not call setStatus here because ws.close() will trigger ws.onclose which handles UI state
        ws.close();
    };
}

function disconnect() {
    reconnectCount = MAX_RECONNECTS; // Block auto-reconnect
    if (ws) ws.close();
}

function cleanupConnection() {
    isConnected       = false;
    lastBytesReceived = 0;
    lastFramesDecoded = 0;
    if (pc) { pc.close(); pc = null; }
    streamVideo.srcObject = null;
}

function showDisconnectedUI(reason = 'Disconnected') {
    setStatus('disconnected', reason);
    connectBtn.innerText     = 'Connect to Server';
    connectBtn.disabled      = false;
    statsPanel.style.display = 'none';
    floatingStats.classList.add('hidden');
    uiOverlay.classList.remove('hidden');
}

function setStatus(cls, text) {
    statusIndicator.className = `status ${cls}`;
    statusIndicator.innerText = text;
}

// ─── SDP helpers ──────────────────────────────────────────────────────────────

/**
 * Inject b=AS:{kbps} and b=TIAS:{bps} after the m=video line.
 * Prevents WebRTC congestion control from throttling below our target bitrate (e.g. 20480 kbps = 20 Mbps).
 * TIAS is required for Firefox/newer Chrome compatibility.
 */
function mutateSdpBandwidth(sdp, kbps) {
    return sdp.replace(/(m=video[^\n]*\n)/g, `$1b=AS:${kbps}\r\nb=TIAS:${kbps * 1000}\r\n`);
}

/**
 * Set codec preferences to prioritize H.264 High Profile.
 * Ensures iPads use hardware H.264 decoding instead of software VP8.
 */
function applyCodecPreferences(transceiver) {
    if (!RTCRtpReceiver.getCapabilities) return;
    try {
        const caps = RTCRtpReceiver.getCapabilities('video');
        if (!caps) return;

        const h264High = caps.codecs.filter(c =>
            c.mimeType.toLowerCase() === 'video/h264' &&
            c.sdpFmtpLine?.toLowerCase().includes('profile-level-id=64'));

        const h264Other = caps.codecs.filter(c =>
            c.mimeType.toLowerCase() === 'video/h264' && !h264High.includes(c));

        const rest = caps.codecs.filter(c =>
            c.mimeType.toLowerCase() !== 'video/h264');

        transceiver.setCodecPreferences([...h264High, ...h264Other, ...rest]);
        console.log('[WebRTC] H.264 High Profile prioritized');
    } catch (err) {
        console.warn('[WebRTC] setCodecPreferences failed:', err.message);
    }
}

// ─── Fullscreen ───────────────────────────────────────────────────────────────
fullscreenBtn.addEventListener('click', () => {
    const elem = document.documentElement;
    const isFS = !!(
        document.fullscreenElement       ||
        document.mozFullScreenElement    ||
        document.webkitFullscreenElement ||
        document.msFullscreenElement
    );
    if (!isFS) {
        (elem.requestFullscreen    ||
         elem.msRequestFullscreen  ||
         elem.mozRequestFullScreen ||
         elem.webkitRequestFullscreen)?.call(elem);
    } else {
        (document.exitFullscreen      ||
         document.msExitFullscreen    ||
         document.mozCancelFullScreen ||
         document.webkitExitFullscreen)?.call(document);
    }
});
