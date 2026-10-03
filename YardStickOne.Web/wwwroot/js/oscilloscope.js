// oscilloscope.js — ES module
//
// Three-panel display:
//
//   ┌──────────────────────────────────────────┬────────────┐
//   │  WAVEFORM PANE  (top 55%)                │  SIDEBAR   │
//   │  1px trace · burst-start ▼ markers       │  burst /   │
//   │  zoom/pan/select · HUD at bottom         │  packet    │
//   ├──────────────────────────────────────────┤  list with │
//   │  RASTER PANE    (bottom 45%)             │  OOK pulse │
//   │  pixel-per-bit history grid              │  analysis  │
//   │  each px = 1 bit  green=HI  dark=LO      │            │
//   │  wraps at canvas width (like waterfall)  │            │
//   └──────────────────────────────────────────┴────────────┘
//
// SignalR is loaded as a global (window.signalR).

// ── ring buffer ───────────────────────────────────────────────────────────────
const CAPACITY = 200_000;   // bytes → up to 1.6 M bit-samples
const buf      = new Uint8Array(CAPACITY);
let   writePos = 0;
let   total    = 0;

function bufWrite(data) {
    for (let i = 0; i < data.length; i++) {
        buf[writePos] = data[i];
        writePos = (writePos + 1) % CAPACITY;
    }
    total += data.length;
}

function byteAt(i) { return buf[i % CAPACITY]; }
function bitAt(bi) {
    return (byteAt(Math.floor(bi / 8)) >> (7 - (bi & 7))) & 1;
}

// ── burst detection ───────────────────────────────────────────────────────────
// A "burst" is a sequence of RF activity separated by >= BURST_GAP_BITS
// consecutive silent (LOW) bits on each side.
const BURST_GAP_BITS = 48;    // ≈ 10 ms silence at 4800 baud marks burst boundary
const MAX_BURSTS     = 150;

const bursts  = [];           // { s, e, bits, pulses }
let   scanPos = 0;            // absolute bit index scanned so far
let   inBurst = false;
let   burstS  = 0;
let   silCnt  = 0;

function scanBursts() {
    // Leave a margin at the live edge so an open burst is not prematurely closed
    const end = Math.max(scanPos, total * 8 - BURST_GAP_BITS * 3);
    for (let bi = scanPos; bi < end; bi++) {
        if (bitAt(bi) === 1) {
            if (!inBurst) { burstS = bi; inBurst = true; }
            silCnt = 0;
        } else {
            silCnt++;
            if (inBurst && silCnt >= BURST_GAP_BITS) {
                closeBurst(burstS, bi - silCnt);
                inBurst = false;
            }
        }
    }
    scanPos = end;
}

function closeBurst(s, e) {
    if (e <= s) return;
    bursts.push({ s, e, bits: e - s, pulses: analyzePulses(s, e) });
    if (bursts.length > MAX_BURSTS) bursts.shift();
    refreshSidebar();
}

// Measure runs of consecutive identical bits within a burst.
// Classify by the ratio of the two most common run lengths.
function analyzePulses(s, e) {
    const runs = [];
    let rb = bitAt(s), rl = 1;
    for (let bi = s + 1; bi <= e; bi++) {
        const b = bi < e ? bitAt(bi) : -1;  // -1 flushes the last run
        if (b === rb) { rl++; } else { runs.push({ bit: rb, len: rl }); rb = b; rl = 1; }
    }
    // Histogram of run lengths (ignore very long runs — likely preamble/silence)
    const freq = {};
    for (const r of runs) if (r.len <= 128) freq[r.len] = (freq[r.len] || 0) + 1;
    const top = Object.entries(freq).sort((a, b) => b[1] - a[1]);
    const t1  = top[0] ? +top[0][0] : 0;
    const t2  = top[1] ? +top[1][0] : 0;
    const ratio = t1 > 0 && t2 > 0 ? Math.max(t1, t2) / Math.min(t1, t2) : 0;
    const type  = runs.length < 4    ? 'noise'
                : ratio >= 1.8 && ratio <= 3.8 ? 'OOK-PWM'
                : ratio <  1.3                  ? 'OOK-PCM'
                :                                 'OOK';
    return { count: runs.length, t1, t2, ratio: ratio.toFixed(1), type };
}

// ── state ─────────────────────────────────────────────────────────────────────
let canvas, ctx, dotnet, sidebarEl, baudInfoEl;
let samplesPerPixel = 4;
let viewOffset      = 0;
let isLive          = true;
let rafId           = null;
let baudRate        = 4800;
let rasterImgData   = null;   // reused ImageData for raster pane

const WAVE_FRAC = 0.55;       // fraction of canvas height used by waveform pane

// Selection (in absolute bit indices)
let selStart = -1, selEnd = -1;

// Mouse interaction
let isDragging = false, isPanning = false;
let dragStartX = 0, dragStartOff = 0;

// SignalR
let hubConn = null;

// ── public API ────────────────────────────────────────────────────────────────

export function init(canvasEl, dotnetRef) {
    canvas     = canvasEl;
    ctx        = canvas.getContext('2d');
    dotnet     = dotnetRef;
    sidebarEl  = document.getElementById('burstList');
    baudInfoEl = document.getElementById('burstBaudInfo');

    canvas.addEventListener('wheel',      onWheel,     { passive: false });
    canvas.addEventListener('mousedown',  onMouseDown);
    canvas.addEventListener('mousemove',  onMouseMove);
    canvas.addEventListener('mouseup',    onMouseUp);
    canvas.addEventListener('mouseleave', onMouseUp);
    window.addEventListener('resize',     onResize);
    onResize();

    rafId = requestAnimationFrame(render);
}

/** Call after capture starts so timing labels are accurate. */
export function setBaudRate(rate) {
    baudRate = rate || 4800;
    if (baudInfoEl) baudInfoEl.textContent = `${baudRate} Bd`;
    refreshSidebar();
}

export async function connectHub(url) {
    if (hubConn) await hubConn.stop();
    const sR = window.signalR;
    if (!sR?.HubConnectionBuilder) throw new Error('SignalR not loaded (window.signalR missing).');
    hubConn = new sR.HubConnectionBuilder()
        .withUrl(url)
        .withAutomaticReconnect()
        .build();
    hubConn.on('ChunkReceived', (data) => {
        const bytes = typeof data === 'string' ? b64ToBytes(data) : new Uint8Array(data);
        bufWrite(bytes);
        scanBursts();
        if (isLive) viewOffset = 0;
    });
    await hubConn.start();
}

export async function disconnectHub() {
    if (hubConn) { await hubConn.stop(); hubConn = null; }
}

export function appendData(data) {
    const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
    bufWrite(bytes);
    scanBursts();
    if (isLive) viewOffset = 0;
}

export function clear() {
    buf.fill(0);
    writePos = 0; total = 0;
    selStart = selEnd = -1;
    viewOffset = 0; isLive = true;
    bursts.length = 0; scanPos = 0; inBurst = false; silCnt = 0;
    rasterImgData = null;
    refreshSidebar();
    dotnet.invokeMethodAsync('OnSelectionChanged', false);
}

/** Returns the selected bytes (bit selection rounded to byte boundaries). */
export function getSelectionData() {
    if (selStart < 0 || selEnd < 0) return new Uint8Array(0);
    const s  = Math.min(selStart, selEnd);
    const e  = Math.max(selStart, selEnd);
    const sb = Math.floor(s / 8);
    const eb = Math.floor(e / 8) + 1;
    const out = new Uint8Array(eb - sb);
    for (let i = 0; i < out.length; i++) out[i] = byteAt(sb + i);
    return out;
}

export function dispose() {
    if (rafId) cancelAnimationFrame(rafId);
    window.removeEventListener('resize', onResize);
    if (hubConn) hubConn.stop();
}

// ── sidebar (HTML panel) ──────────────────────────────────────────────────────

function refreshSidebar() {
    if (!sidebarEl) return;
    if (bursts.length === 0) {
        sidebarEl.innerHTML = '<div class="burst-empty">No bursts detected yet.<br><small>Waiting for RF activity...</small></div>';
        return;
    }
    const bpms = baudRate / 1000;   // bits per millisecond
    const rows = bursts.slice(-80).reverse().map((b, ri) => {
        const num = bursts.length - ri;
        const ms  = (b.bits / bpms).toFixed(1);
        const p   = b.pulses;
        const tc  = p.type === 'OOK-PWM' ? 'pwm'
                  : p.type === 'OOK-PCM' ? 'pcm'
                  : p.type === 'OOK'     ? 'ook'
                  :                        'noise';
        const timing = tc !== 'noise'
            ? `<div class="burst-detail">T1=${p.t1}b  T2=${p.t2}b  ratio=${p.ratio}</div>`
            : '';
        return `<div class="burst-entry burst-${tc}"><div class="burst-head"><span class="burst-num">#${num}</span><span class="burst-type">${p.type}</span></div><div class="burst-detail">${ms} ms  ${b.bits} bits  ${p.count} pulses</div>${timing}</div>`;
    });
    sidebarEl.innerHTML = rows.join('');
}

// ── render loop ───────────────────────────────────────────────────────────────

function render() {
    rafId = requestAnimationFrame(render);

    const W     = canvas.width;
    const H     = canvas.height;
    const waveH = Math.round(H * WAVE_FRAC);
    const rastH = H - waveH;

    ctx.fillStyle = '#0d1117';
    ctx.fillRect(0, 0, W, H);

    if (total === 0) {
        ctx.fillStyle = '#555';
        ctx.font      = '14px monospace';
        ctx.textAlign = 'center';
        ctx.fillText('No data — click Start to begin capture', W / 2, H / 2);
        return;
    }

    drawWaveform(W, waveH);
    drawRaster(W, waveH, rastH);
}

// ── waveform pane ─────────────────────────────────────────────────────────────

function drawWaveform(W, H) {
    const totalBits = total * 8;
    const oldestBit = Math.max(0, totalBits - CAPACITY * 8);
    const rightBit  = Math.max(0, totalBits - viewOffset);
    const leftBit   = Math.max(oldestBit, rightBit - Math.ceil(W * samplesPerPixel));

    const pad  = Math.max(18, H * 0.15);
    const yHi  = pad;
    const yLo  = H - pad - 6;
    const yMid = (yHi + yLo) / 2;

    // grid lines
    ctx.setLineDash([3, 9]);
    ctx.strokeStyle = 'rgba(255,255,255,0.05)';
    ctx.lineWidth   = 1;
    ctx.beginPath();
    [yHi, yMid, yLo].forEach(y => { ctx.moveTo(0, y); ctx.lineTo(W, y); });
    ctx.stroke();
    ctx.setLineDash([]);

    // level labels
    ctx.fillStyle = '#2d4030';
    ctx.font      = 'bold 9px monospace';
    ctx.textAlign = 'left';
    ctx.fillText('1', 4, yHi - 2);
    ctx.fillText('0', 4, yLo + 9);

    // burst-start markers: orange downward triangles at top of pane
    for (const burst of bursts) {
        const bx = Math.round((burst.s - leftBit) / samplesPerPixel);
        if (bx < 0 || bx >= W) continue;
        ctx.fillStyle = '#ff8c00';
        ctx.beginPath();
        ctx.moveTo(bx - 3, 1);
        ctx.lineTo(bx + 3, 1);
        ctx.lineTo(bx, 8);
        ctx.closePath();
        ctx.fill();
    }

    // waveform trace — 1px green
    ctx.strokeStyle = '#39d353';
    ctx.lineWidth   = 1;
    ctx.beginPath();
    let prevB = -1, started = false;
    for (let px = 0; px < W; px++) {
        const bi = Math.round(leftBit + px * samplesPerPixel);
        if (bi < oldestBit || bi >= totalBits) continue;
        let b;
        if (samplesPerPixel > 1) {
            let sum = 0;
            const n = Math.min(Math.ceil(samplesPerPixel), totalBits - bi);
            for (let k = 0; k < n; k++) sum += bitAt(bi + k);
            b = sum >= n / 2 ? 1 : 0;
        } else {
            b = bitAt(bi);
        }
        const y = b ? yHi : yLo;
        if (!started) { ctx.moveTo(px, y); started = true; }
        else if (b !== prevB) { ctx.lineTo(px, prevB ? yHi : yLo); ctx.lineTo(px, y); }
        ctx.lineTo(px + 1, y);
        prevB = b;
    }
    ctx.stroke();

    // selection overlay
    if (selStart >= 0 && selEnd >= 0) {
        const s  = Math.min(selStart, selEnd);
        const e  = Math.max(selStart, selEnd);
        const x1 = Math.max(0, Math.floor((s - leftBit) / samplesPerPixel));
        const x2 = Math.min(W, Math.ceil( (e - leftBit) / samplesPerPixel));
        ctx.fillStyle   = 'rgba(80,140,255,0.2)';
        ctx.fillRect(x1, 0, x2 - x1, H);
        ctx.strokeStyle = 'rgba(80,140,255,0.65)';
        ctx.lineWidth   = 1;
        ctx.beginPath();
        ctx.moveTo(x1, 0); ctx.lineTo(x1, H);
        ctx.moveTo(x2, 0); ctx.lineTo(x2, H);
        ctx.stroke();
    }

    // HUD — bottom of waveform pane
    const bitsVis  = Math.ceil(W * samplesPerPixel);
    const zoomLbl  = samplesPerPixel >= 1
        ? `${samplesPerPixel.toFixed(1)}samp/px`
        : `${(1 / samplesPerPixel).toFixed(1)}px/samp`;
    const usPerBit = (1e6 / baudRate).toFixed(0);
    ctx.fillStyle = 'rgba(80,80,100,0.9)';
    ctx.font      = '10px monospace';
    ctx.textAlign = 'left';
    ctx.fillText(`${zoomLbl}  ${bitsVis.toLocaleString()}bits  ${(total / 1000).toFixed(1)}KB  ${usPerBit}us/bit @ ${baudRate}Bd`, 6, H - 4);

    ctx.font      = 'bold 10px monospace';
    ctx.textAlign = 'right';
    ctx.fillStyle = isLive ? '#39d353' : '#58a6ff';
    ctx.fillText(isLive ? '* LIVE' : '| PAUSED', W - 6, 12);

    if (bursts.length > 0) {
        ctx.fillStyle = '#ff8c00';
        ctx.font      = '10px monospace';
        ctx.fillText(`^ ${bursts.length}`, W - 6, 24);
    }

    if (selStart >= 0 && selEnd >= 0) {
        const bits = Math.abs(selEnd - selStart) + 1;
        ctx.fillStyle = 'rgba(80,140,255,0.9)';
        ctx.font      = '10px monospace';
        ctx.textAlign = 'center';
        ctx.fillText(`sel: ${bits.toLocaleString()} bits (${Math.ceil(bits / 8)} B)`, W / 2, H - 4);
    }

    // pane separator
    ctx.strokeStyle = 'rgba(255,255,255,0.10)';
    ctx.lineWidth   = 1;
    ctx.beginPath();
    ctx.moveTo(0, H - 0.5); ctx.lineTo(W, H - 0.5);
    ctx.stroke();
}

// ── raster pane ───────────────────────────────────────────────────────────────
// Each pixel = 1 bit: green = carrier HIGH, near-black = LOW.
// Rows wrap left-to-right then top-to-bottom (oldest top-left, newest bottom-right).
// Orange pixels mark burst starts.

function drawRaster(W, yOff, H) {
    if (H < 4 || W < 4) return;

    const totalBits   = total * 8;
    const oldestBit   = Math.max(0, totalBits - CAPACITY * 8);
    const rightBit    = Math.max(0, totalBits - viewOffset);
    const rasterStart = rightBit - H * W;  // bit index at top-left pixel

    if (!rasterImgData || rasterImgData.width !== W || rasterImgData.height !== H) {
        rasterImgData = ctx.createImageData(W, H);
    }
    const d = rasterImgData.data;

    for (let row = 0; row < H; row++) {
        const rowBase = rasterStart + row * W;
        for (let col = 0; col < W; col++) {
            const bi = rowBase + col;
            const px = (row * W + col) * 4;
            if (bi < oldestBit || bi < 0 || bi >= totalBits) {
                d[px] = 13; d[px + 1] = 17; d[px + 2] = 23; d[px + 3] = 255;
            } else if (bitAt(bi) === 1) {
                d[px] = 57; d[px + 1] = 211; d[px + 2] = 83; d[px + 3] = 255;
            } else {
                d[px] = 13; d[px + 1] = 17; d[px + 2] = 23; d[px + 3] = 255;
            }
        }
    }

    // Overlay burst-start pixels in orange
    for (const burst of bursts) {
        const bi = burst.s;
        if (bi < rasterStart || bi >= rightBit) continue;
        const rel = bi - rasterStart;
        const row = Math.floor(rel / W);
        const col = rel % W;
        if (row < 0 || row >= H || col < 0 || col >= W) continue;
        const px = (row * W + col) * 4;
        d[px] = 255; d[px + 1] = 140; d[px + 2] = 0; d[px + 3] = 255;
    }

    ctx.putImageData(rasterImgData, 0, yOff);

    // raster label
    const totalSecs = ((H * W) / baudRate).toFixed(0);
    ctx.fillStyle = 'rgba(55,55,75,0.95)';
    ctx.font      = '10px monospace';
    ctx.textAlign = 'left';
    ctx.fillText(`raster  ${H}rows x ${W}bits/row  ~${totalSecs}s @ ${baudRate}Bd`, 4, yOff + 11);
}

// ── coordinate helpers ────────────────────────────────────────────────────────

function pixelToBit(px) {
    const tb       = total * 8;
    const rightBit = Math.max(0, tb - viewOffset);
    const leftBit  = Math.max(0, rightBit - canvas.width * samplesPerPixel);
    return Math.round(leftBit + px * samplesPerPixel);
}

// ── input handlers ────────────────────────────────────────────────────────────

function onWheel(e) {
    e.preventDefault();
    if (e.shiftKey) {
        const step = Math.ceil(canvas.width * samplesPerPixel * 0.3);
        viewOffset = clamp(viewOffset + (e.deltaY > 0 ? step : -step), 0, total * 8);
        isLive = viewOffset === 0;
    } else {
        samplesPerPixel = clamp(samplesPerPixel * (e.deltaY < 0 ? 1 / 1.3 : 1.3), 0.25, 512);
    }
}

function onMouseDown(e) {
    const rect   = canvas.getBoundingClientRect();
    dragStartX   = e.clientX - rect.left;
    dragStartOff = viewOffset;
    isDragging   = true;
    if (e.ctrlKey || e.metaKey) {
        isPanning = true;
        canvas.style.cursor = 'grabbing';
    } else {
        isPanning = false;
        selStart  = pixelToBit(dragStartX);
        selEnd    = selStart;
        dotnet.invokeMethodAsync('OnSelectionChanged', false);
        canvas.style.cursor = 'crosshair';
    }
}

function onMouseMove(e) {
    if (!isDragging) return;
    const rect = canvas.getBoundingClientRect();
    const px   = e.clientX - rect.left;
    if (isPanning) {
        viewOffset = clamp(dragStartOff - (px - dragStartX) * samplesPerPixel, 0, total * 8);
        isLive = viewOffset === 0;
    } else {
        selEnd = pixelToBit(px);
        dotnet.invokeMethodAsync('OnSelectionChanged', selStart !== selEnd);
    }
}

function onMouseUp() {
    isDragging = isPanning = false;
    canvas.style.cursor = 'default';
    if (selStart >= 0 && selEnd >= 0)
        dotnet.invokeMethodAsync('OnSelectionChanged', selStart !== selEnd);
}

function onResize() {
    if (!canvas) return;
    canvas.width  = canvas.parentElement?.clientWidth  || window.innerWidth;
    canvas.height = canvas.parentElement?.clientHeight || (window.innerHeight - 56);
    rasterImgData = null;
}

// ── utilities ─────────────────────────────────────────────────────────────────

function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

function b64ToBytes(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}
