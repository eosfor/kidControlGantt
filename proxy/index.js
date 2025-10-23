const express = require('express');
const fetch = (...args) => import('node-fetch').then(m => m.default(...args));
const app = express();

const TARGET = process.env.TARGET || 'http://192.168.88.254/rest/ip/kid-control';
const BASIC_AUTH = process.env.BASIC_AUTH || ''; // 'Basic base64(user:pass)'

app.get('/api/kid-control', async (req, res) => {
  try {
    const r = await fetch(TARGET, {
      method: 'GET',
      headers: {
        'Accept': 'application/json',
        ...(BASIC_AUTH ? { 'Authorization': BASIC_AUTH } : {})
      }
    });

    if (!r.ok) {
      const text = await r.text();
      return res.status(502).json({ error: `Upstream ${r.status}`, body: text });
    }

    const json = await r.json();
    res.json(json);
  } catch (err) {
    console.error('proxy error', err);
    res.status(502).json({ error: err.message });
  }
});

app.get('/health', (req, res) => res.send('OK'));

// Debug endpoint (safe): checks upstream reachability and whether BASIC_AUTH is configured.
// Does NOT return the BASIC_AUTH value itself.
app.get('/api/debug', async (req, res) => {
  try {
    const r = await fetch(TARGET, {
      method: 'GET',
      headers: {
        'Accept': 'application/json',
        ...(BASIC_AUTH ? { 'Authorization': BASIC_AUTH } : {})
      }
    });

    const text = await r.text().catch(() => '');
    // return minimal information useful for debugging without leaking secrets
    return res.json({
      upstreamStatus: r.status,
      upstreamOk: r.ok,
      authProvided: !!BASIC_AUTH,
      authLength: BASIC_AUTH ? BASIC_AUTH.length : 0,
      bodySnippet: text ? text.substring(0, 256) : ''
    });
  } catch (err) {
    console.error('debug fetch error', err && err.message ? err.message : err);
    return res.status(502).json({ error: 'upstream fetch failed', message: err.message || String(err) });
  }
});

const port = process.env.PORT || 4000;
app.listen(port, () => console.log(`Proxy listening on ${port}`));
