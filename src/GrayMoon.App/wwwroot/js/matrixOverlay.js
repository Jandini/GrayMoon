(function () {
  const state = new Map(); // canvasId -> state object

  function resize(canvas, ctx) {
    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    const w = Math.max(1, Math.floor(rect.width));
    const h = Math.max(1, Math.floor(rect.height));

    canvas.width = Math.floor(w * dpr);
    canvas.height = Math.floor(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    return { w, h };
  }

  function randomChar() {
    const pools = [
      "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
      "!@#$%^&*()-_=+[]{};:,.<>/?\\|",
      "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ"
    ];
    const p = pools[(Math.random() * pools.length) | 0];
    return p[(Math.random() * p.length) | 0];
  }

  function start(canvasId, options) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    stop(canvasId); // restart cleanly if already running

    const ctx = canvas.getContext("2d", { alpha: true, desynchronized: true });
    if (!ctx) return;

    const fontSize = Math.max(10, (options?.fontSize ?? 16) | 0);
    const frameIntervalMs = Math.max(16, (options?.frameIntervalMs ?? 33) | 0);
    const fadeAlpha = Math.min(1, Math.max(0, options?.fadeAlpha ?? 0.08)); // trail length
    const characterOpacity = Math.min(1, Math.max(0, options?.characterOpacity ?? 0.65));

    let { w, h } = resize(canvas, ctx);

    const columns = Math.max(1, Math.floor(w / fontSize));
    const drops = new Array(columns).fill(0).map(() => (Math.random() * h) / fontSize);

    ctx.font = `${fontSize}px ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace`;
    ctx.textBaseline = "top";

    const onResize = () => {
      ({ w, h } = resize(canvas, ctx));
      const newColumns = Math.max(1, Math.floor(w / fontSize));
      if (newColumns !== drops.length) {
        drops.length = newColumns;
        for (let i = 0; i < newColumns; i++) {
          if (typeof drops[i] !== "number") drops[i] = (Math.random() * h) / fontSize;
        }
      }
      ctx.font = `${fontSize}px ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace`;
    };

    window.addEventListener("resize", onResize);

    const s = {
      running: true,
      canvas,
      ctx,
      drops,
      fontSize,
      frameIntervalMs,
      fadeAlpha,
      characterOpacity,
      onResize,
      timerId: null
    };

    // Throttled loop (stable FPS, less battery use than full raf)
    s.timerId = window.setInterval(() => {
      if (!s.running) return;

      // translucent black fill = trail
      ctx.fillStyle = `rgba(0, 0, 0, ${fadeAlpha})`;
      ctx.fillRect(0, 0, w, h);

      for (let i = 0; i < drops.length; i++) {
        const x = i * fontSize;
        const y = drops[i] * fontSize;

        const c = randomChar();

        // Grayscale intensity range (brighter + dimmer variation)
        const intensity = 180 + ((Math.random() * 75) | 0);
        ctx.fillStyle = `rgba(${intensity}, ${intensity}, ${intensity}, ${characterOpacity})`;
        ctx.fillText(c, x, y);

        // reset occasionally for randomness
        if (y > h && Math.random() > 0.975) drops[i] = 0;
        else drops[i] += 1;
      }
    }, frameIntervalMs);

    state.set(canvasId, s);
  }

  function stop(canvasId) {
    const s = state.get(canvasId);
    if (!s) return;

    s.running = false;
    if (s.timerId) window.clearInterval(s.timerId);
    window.removeEventListener("resize", s.onResize);

    // clear canvas
    try {
      const rect = s.canvas.getBoundingClientRect();
      s.ctx.clearRect(0, 0, rect.width, rect.height);
    } catch { /* ignore */ }

    state.delete(canvasId);
  }

  function setDim(canvasId, opacity) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const root = canvas.closest(".loading-overlay");
    if (!root) return;

    const v = Math.min(1, Math.max(0, opacity));
    root.style.setProperty("--overlay-dim", String(v));
  }

  window.matrixOverlay = { start, stop, setDim };
})();
