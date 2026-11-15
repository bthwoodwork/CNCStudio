namespace CNCStudio.Core.Remote;

/// <summary>The phone's page: one self-contained HTML file (no internet needed), polling the status twice a second.</summary>
public static class RemotePage
{
    public const string Html = """
<!doctype html>
<html lang="sv">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<meta name="theme-color" content="#17212c">
<title>CNC Studio</title>
<style>
:root { --bg:#17212c; --card:#1f2d3c; --ink:#e8f1fa; --muted:#8ca3ba; --line:#33475c; --go:#2e7a4a; --stop:#b3362b; --blue:#24618c; }
* { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
body { margin:0; background:var(--bg); color:var(--ink); font:16px system-ui, -apple-system, "Segoe UI", sans-serif; padding:12px 16px 40px; }
h1 { font-size:17px; margin:0 0 8px; display:flex; justify-content:space-between; align-items:center; }
.state { font-size:13px; padding:3px 10px; border-radius:20px; background:var(--line); }
.state.Run { background:var(--go); } .state.Hold, .state.Alarm { background:var(--stop); }
.card { background:var(--card); border-radius:12px; padding:12px; margin:10px 0; }
.dro { display:grid; grid-template-columns:repeat(3,1fr); gap:6px; text-align:center; }
.dro b { display:block; font:600 22px ui-monospace, Consolas, monospace; }
.dro span { color:var(--muted); font-size:12px; }
button { font:600 16px system-ui, sans-serif; color:var(--ink); background:#2a3c50; border:1px solid var(--line); border-radius:10px; padding:14px 6px; min-height:52px; touch-action:manipulation; }
button:active { filter:brightness(1.4); }
button.sel { background:var(--blue); }
.grid3 { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }
.grid2 { display:grid; grid-template-columns:repeat(2,1fr); gap:8px; }
.jog { display:grid; grid-template-columns:repeat(3,1fr) 12px 1fr; gap:8px; }
.stop { width:100%; background:var(--stop); font-size:22px; min-height:68px; margin-top:4px; }
.go { background:var(--go); }
.label { color:var(--muted); font-size:12px; margin:2px 0 6px; text-transform:uppercase; letter-spacing:.05em; }
.msg { color:#ffb38a; font-size:14px; min-height:20px; margin-top:6px; }
.bar { height:8px; background:#24384c; border-radius:4px; overflow:hidden; margin-top:6px; }
.bar i { display:block; height:100%; background:#4ce07a; width:0; }
.row { display:flex; justify-content:space-between; align-items:center; gap:8px; }
.small button { min-height:44px; padding:8px 4px; font-size:14px; }
#pinbox { display:none; }
input { font:600 24px ui-monospace, monospace; width:100%; padding:10px; border-radius:10px; border:1px solid var(--line); background:#10202e; color:var(--ink); letter-spacing:.3em; text-align:center; }
</style>
</head>
<body>
<h1>CNC Studio <span id="state" class="state">–</span></h1>

<div id="pinbox" class="card">
  <div class="label">PIN från datorn (Maskin → Styr från mobilen)</div>
  <input id="pin" inputmode="numeric" maxlength="6" autocomplete="off">
  <button class="go" style="width:100%;margin-top:8px" onclick="savePin()">OK</button>
</div>

<div id="main">
  <button class="stop" onclick="cmd('stop')">STOPP</button>
  <div class="card">
    <div class="dro"><div><span>X</span><b id="x">–</b></div><div><span>Y</span><b id="y">–</b></div><div><span>Z</span><b id="z">–</b></div></div>
    <div class="msg" id="msg"></div>
  </div>

  <div class="card" id="jobcard">
    <div class="row"><div class="label" id="job">Inget program körs</div><div class="label" id="pct"></div></div>
    <div class="bar"><i id="bar"></i></div>
    <div class="grid3" style="margin-top:10px">
      <button onclick="cmd('pause')">❚❚ Paus</button>
      <button class="go" onclick="cmd('resume')">▶ Fortsätt</button>
      <button id="startbtn" onclick="if(confirm('Starta programmet som är laddat i CNC Studio?'))cmd('start')">▶ Starta</button>
    </div>
  </div>

  <div class="card">
    <div class="label">Jogga – ett tryck = ett steg</div>
    <div class="jog">
      <button onclick="jog('X-Y+')">↖</button><button onclick="jog('Y+')">Y+</button><button onclick="jog('X+Y+')">↗</button><div></div><button onclick="jog('Z+')">Z+</button>
      <button onclick="jog('X-')">X−</button><button onclick="cmd('jogstop')">■</button><button onclick="jog('X+')">X+</button><div></div><div></div>
      <button onclick="jog('X-Y-')">↙</button><button onclick="jog('Y-')">Y−</button><button onclick="jog('X+Y-')">↘</button><div></div><button onclick="jog('Z-')">Z−</button>
    </div>
    <div class="grid3" style="margin-top:8px">
      <button id="sp0" onclick="speed('precise')">Precis</button><button id="sp1" onclick="speed('normal')">Normal</button><button id="sp2" onclick="speed('rapid')">Snabb</button>
    </div>
  </div>

  <div class="card small">
    <div class="label">Nolla och gå till</div>
    <div class="grid3">
      <button onclick="if(confirm('Nolla X och Y där fräsen står?'))cmd('zero',{axes:'XY'})">Nolla XY</button>
      <button onclick="if(confirm('Nolla Z där fräsen står?'))cmd('zero',{axes:'Z'})">Nolla Z</button>
      <button onclick="cmd('goto0')">Till X0 Y0</button>
      <button onclick="if(confirm('Kör homing? Maskinen åker till brytarna.'))cmd('home')">⌂ Homing</button>
      <button onclick="cmd('unlock')">Lås upp $X</button>
      <button id="flood" onclick="cmd('flood')">Kylning</button>
    </div>
    <div id="locs" class="grid2" style="margin-top:8px"></div>
  </div>

  <div class="card small">
    <div class="row"><div class="label">Matning</div><div class="label" id="ovf">100 %</div></div>
    <div class="grid3"><button onclick="cmd('feed',{by:'-10'})">−10</button><button onclick="cmd('feed',{by:'reset'})">100 %</button><button onclick="cmd('feed',{by:'+10'})">+10</button></div>
    <div class="row" style="margin-top:8px"><div class="label">Spindel</div><div class="label" id="ovs">100 %</div></div>
    <div class="grid3"><button onclick="cmd('spindleov',{by:'-10'})">−10</button><button onclick="cmd('spindleov',{by:'reset'})">100 %</button><button onclick="cmd('spindleov',{by:'+10'})">+10</button></div>
    <div class="row" style="margin-top:8px"><div class="label">Snabbgång</div><div class="label" id="ovr">100 %</div></div>
    <div class="grid3"><button onclick="cmd('rapid',{to:'25'})">25 %</button><button onclick="cmd('rapid',{to:'50'})">50 %</button><button onclick="cmd('rapid',{to:'100'})">100 %</button></div>
  </div>
</div>

<script>
let pin = new URLSearchParams(location.search).get('pin') || localStorage.getItem('cncpin') || '';
if (new URLSearchParams(location.search).get('pin')) { try { localStorage.setItem('cncpin', pin); } catch (e) {} }
const $ = id => document.getElementById(id);
function needPin(show) { $('pinbox').style.display = show ? 'block' : 'none'; $('main').style.display = show ? 'none' : 'block'; }
function savePin() { pin = $('pin').value.trim(); try { localStorage.setItem('cncpin', pin); } catch (e) {} needPin(false); poll(); }
function msg(t) { $('msg').textContent = t || ''; }
async function cmd(action, args) {
  const body = new URLSearchParams(Object.assign({ pin, action }, args || {}));
  try {
    const r = await fetch('/api/cmd', { method: 'POST', body });
    const j = await r.json();
    if (r.status === 401) { needPin(true); return; }
    msg(j.ok ? '' : j.error);
    if (navigator.vibrate && action === 'stop') navigator.vibrate(80);
  } catch (e) { msg('Ingen kontakt med datorn.'); }
  poll();
}
function jog(dir) { cmd('jog', { dir }); }
function speed(s) { cmd('speed', { to: s }); }
const f = v => v == null ? '–' : v.toFixed(3);
let last = '';
async function poll() {
  if (!pin) { needPin(true); return; }
  try {
    const r = await fetch('/api/status?pin=' + encodeURIComponent(pin));
    if (r.status === 401) { needPin(true); return; }
    const s = await r.json();
    const st = $('state'); st.textContent = s.connected ? s.state : 'Ej ansluten'; st.className = 'state ' + (s.state || '').split(':')[0];
    const p = s.workPos || [];
    $('x').textContent = f(p[0]); $('y').textContent = f(p[1]); $('z').textContent = f(p[2]);
    $('job').textContent = s.job || 'Inget program körs';
    $('pct').textContent = s.jobProgress != null ? Math.round(s.jobProgress * 100) + ' %' : '';
    $('bar').style.width = (s.jobProgress || 0) * 100 + '%';
    $('startbtn').style.display = s.startAllowed ? '' : 'none';
    const ov = s.overrides || [100, 100, 100];
    $('ovf').textContent = ov[0] + ' %'; $('ovr').textContent = ov[1] + ' %'; $('ovs').textContent = ov[2] + ' %';
    $('flood').className = s.floodOn ? 'sel' : '';
    ['precise', 'normal', 'rapid'].forEach((k, i) => $('sp' + i).className = s.jogSpeed === k ? 'sel' : '');
    const locs = JSON.stringify(s.locations);
    if (locs !== last) {
      last = locs;
      $('locs').innerHTML = '';
      (s.locations || []).forEach(n => { const b = document.createElement('button'); b.textContent = n; b.onclick = () => cmd('location', { name: n }); $('locs').appendChild(b); });
    }
    if (s.message && !$('msg').textContent) msg(s.message);
  } catch (e) { $('state').textContent = 'Ingen kontakt'; }
}
needPin(!pin);
setInterval(poll, 500); poll();
</script>
</body>
</html>
""";
}
