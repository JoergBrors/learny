namespace InviteRegistration.Portal;

/// <summary>Stil des Portals. Farben und Abstände folgen dem Learny-Design (src/LearnCards.Web/wwwroot/app.css: --bg, --ink, --brand-600/700, --border, --danger). Kein externes Material.</summary>
public static class PortalCss
{
    public const string Bg = "#F7F5F0";
    public const string Ink = "#29323C";
    public const string InkSoft = "#5B6470";
    public const string Brand600 = "#1F8C82";
    public const string Brand700 = "#196F67";
    public const string Border = "#E4E0D6";
    public const string Danger = "#C4554D";

    public static readonly string Text = $$"""
        :root { --bg:{{Bg}}; --ink:{{Ink}}; --ink-soft:{{InkSoft}}; --brand-600:{{Brand600}}; --brand-700:{{Brand700}}; --border:{{Border}}; --danger:{{Danger}}; --surface:#FFFFFF; --ok:#2E9E6B; --warn:#C97B2D; }
        * { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; }
        body { margin:0; background:var(--bg); color:var(--ink); font:16px/1.5 "Segoe UI", system-ui, -apple-system, Arial, sans-serif; }
        a { color:var(--brand-700); }
        a:focus-visible, button:focus-visible, input:focus-visible, select:focus-visible, textarea:focus-visible { outline:2px solid var(--brand-600); outline-offset:2px; border-radius:4px; }
        header.top { background:var(--surface); border-bottom:1px solid var(--border); }
        .top-inner { max-width:70rem; margin:0 auto; padding:.6rem 1rem; display:flex; align-items:center; gap:1rem; flex-wrap:wrap; }
        .brand { display:flex; align-items:center; text-decoration:none; font-weight:700; font-size:1.2rem; color:var(--ink); }
        .brand img { display:block; height:28px; width:auto; }
        nav.main { display:flex; gap:.25rem .9rem; flex-wrap:wrap; flex:1; }
        nav.main a { text-decoration:none; padding:.25rem .1rem; border-bottom:2px solid transparent; color:var(--ink); }
        nav.main a:hover { border-bottom-color:var(--brand-600); }
        .who { display:flex; align-items:center; gap:.6rem; color:var(--ink-soft); font-size:.9rem; }
        main { max-width:70rem; margin:0 auto; padding:1.25rem 1rem 3rem; }
        h1 { font-size:1.5rem; margin:.2rem 0 1rem; }
        h2 { font-size:1.15rem; margin:1.4rem 0 .6rem; }
        .card { background:var(--surface); border:1px solid var(--border); border-radius:12px; padding:1.1rem 1.25rem; margin-bottom:1rem; }
        .narrow { max-width:34rem; margin-left:auto; margin-right:auto; }
        .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(9.5rem,1fr)); gap:.75rem; margin-bottom:1rem; }
        .stat { background:var(--surface); border:1px solid var(--border); border-radius:12px; padding:.8rem 1rem; }
        .stat b { display:block; font-size:1.6rem; line-height:1.2; color:var(--brand-700); }
        .stat span { color:var(--ink-soft); font-size:.88rem; }
        .table-wrap { overflow-x:auto; background:var(--surface); border:1px solid var(--border); border-radius:12px; }
        table { border-collapse:collapse; width:100%; font-size:.93rem; }
        th, td { text-align:left; padding:.55rem .75rem; border-bottom:1px solid var(--border); vertical-align:top; }
        th { color:var(--ink-soft); font-weight:600; white-space:nowrap; }
        tr:last-child td { border-bottom:none; }
        label { display:block; font-weight:600; margin:.8rem 0 .25rem; }
        input[type=text], input[type=email], input[type=number], input[type=password], select, textarea { width:100%; max-width:36rem; padding:.5rem .6rem; border:1px solid #A9B0BA; border-radius:8px; font:inherit; background:#fff; color:var(--ink); }
        .checks label { font-weight:400; margin:.3rem 0; display:flex; gap:.5rem; align-items:center; }
        .btn { display:inline-block; padding:.5rem .95rem; border-radius:8px; border:1px solid var(--brand-700); background:var(--brand-700); color:#fff; font:inherit; cursor:pointer; text-decoration:none; }
        .btn.ghost { background:#fff; color:var(--brand-700); }
        .btn.danger { background:#fff; color:#9A3029; border-color:#9A3029; }
        .btn.small { padding:.25rem .6rem; font-size:.88rem; }
        form.inline { display:inline; }
        .row { display:flex; flex-wrap:wrap; gap:.5rem; align-items:center; }
        .badge { display:inline-block; padding:.05rem .55rem; border-radius:999px; font-size:.8rem; border:1px solid var(--border); background:#fff; white-space:nowrap; }
        .badge.ok { border-color:#2E9E6B; color:#1E6E48; } .badge.warn { border-color:var(--warn); color:#8A5214; } .badge.bad { border-color:#9A3029; color:#9A3029; }
        .msg { padding:.6rem .9rem; border-radius:8px; border:1px solid var(--border); background:#fff; margin-bottom:1rem; }
        .msg.ok { border-color:#2E9E6B; } .msg.err { border-color:#9A3029; }
        .muted { color:var(--ink-soft); font-size:.9rem; }
        ul.timeline { list-style:none; padding:0; margin:0; }
        ul.timeline li { display:flex; gap:.75rem; padding:.4rem 0; border-bottom:1px solid var(--border); }
        ul.timeline li:last-child { border-bottom:none; }
        ul.timeline time { color:var(--ink-soft); min-width:9.5rem; font-variant-numeric:tabular-nums; }
        .filters { display:flex; flex-wrap:wrap; gap:.5rem; align-items:end; margin-bottom:.8rem; }
        .filters select, .filters input { max-width:13rem; }
        @media (max-width:40rem) { main { padding:1rem .75rem 2.5rem; } h1 { font-size:1.3rem; } .who { width:100%; } }
        """;
}
