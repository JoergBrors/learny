// Einladungslink: /invite#<token>. Das Fragment wird nie an Server, Proxy oder Protokolle gesendet.
// Der Token wird sofort aus der Adressleiste entfernt und nur per POST-Body an /invite/start übergeben (ADR 0049).
(function () {
  var raw = (location.hash || '').slice(1);
  try { history.replaceState(null, '', location.pathname); } catch (e) { /* ohne History-API: Fragment bleibt nur lokal */ }
  var m = /^[A-Za-z0-9_-]{20,128}$/.exec(raw);
  var msg = document.getElementById('msg');
  if (!m) {
    if (msg) msg.textContent = 'Der Einladungslink ist unvollständig. Bitte öffnen Sie den vollständigen Link aus der E-Mail.';
    return;
  }
  var f = document.createElement('form');
  f.method = 'post';
  f.action = '/invite/start';
  var i = document.createElement('input');
  i.type = 'hidden'; i.name = 'token'; i.value = m[0];
  f.appendChild(i);
  document.body.appendChild(f);
  f.submit();
})();
