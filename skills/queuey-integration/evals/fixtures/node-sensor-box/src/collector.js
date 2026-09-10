import { fetch } from "undici";

const pending = []; // in memory; lost on restart

async function flush() {
  while (pending.length) {
    const reading = pending[0];
    try {
      const res = await fetch("https://partner.example/readings", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(reading),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      pending.shift();
    } catch (err) {
      console.warn("send failed, will retry in 30s", err.message);
      await new Promise((r) => setTimeout(r, 30_000));
    }
  }
}

setInterval(() => {
  pending.push({ machine: "press-3", tag: "temp", value: 71.2, at: new Date().toISOString() });
  flush();
}, 5_000);
