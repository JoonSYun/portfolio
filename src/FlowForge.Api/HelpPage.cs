namespace FlowForge.Api;

/// <summary>A tiny self-documenting landing page served at <c>/</c>.</summary>
internal static class HelpPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>FlowForge control plane</title>
        <style>
          body{font:15px/1.6 ui-monospace,SFMono-Regular,Menlo,monospace;max-width:820px;margin:40px auto;padding:0 20px;color:#e7e7e7;background:#111}
          h1{font-size:20px} a{color:#7ab7ff} code{background:#1e1e1e;padding:2px 6px;border-radius:4px}
          .m{color:#8f8}.g{color:#888} table{border-collapse:collapse;width:100%;margin:12px 0}
          td{padding:6px 10px;border-bottom:1px solid #2a2a2a;vertical-align:top}
        </style></head>
        <body>
        <h1>FlowForge — control plane</h1>
        <p class="g">Distributed job scheduling &amp; orchestration platform (reference build, in-memory infra).</p>
        <table>
          <tr><td class="m">GET</td><td><a href="/jobs">/jobs</a></td><td>definitions + per-tenant effective configs</td></tr>
          <tr><td class="m">GET</td><td><a href="/jobs/order.sync/next">/jobs/{key}/next</a></td><td>upcoming scheduled runs</td></tr>
          <tr><td class="m">POST</td><td>/jobs/{key}/trigger?tenant=</td><td>manual trigger (omit tenant to fan out to all)</td></tr>
          <tr><td class="m">GET</td><td><a href="/executions">/executions?job=&amp;tenant=&amp;limit=</a></td><td>execution history, newest first</td></tr>
          <tr><td class="m">GET</td><td><a href="/control">/control</a></td><td>operational snapshot</td></tr>
          <tr><td class="m">POST</td><td>/control/pause · /control/resume</td><td>global pause (tier 1)</td></tr>
          <tr><td class="m">POST</td><td>/control/block?job=&amp;tenant= · /unblock</td><td>domain/tenant block (tier 2)</td></tr>
          <tr><td class="m">POST</td><td>/control/validation?enabled=true</td><td>dry-run validation mode (tier 3)</td></tr>
          <tr><td class="m">GET</td><td><a href="/health">/health</a></td><td>health + bound job keys</td></tr>
        </table>
        <p class="g">Try: <code>curl -X POST localhost:8080/jobs/settlement.close/trigger</code>
        then <code>curl localhost:8080/executions</code></p>
        </body></html>
        """;
}
