// Captures the dashboard screenshots in this folder from the real gateway, against a synthetic LM Studio.
//
// Nothing here comes from a real session: the prompts, the answer, the token counts, and the timings
// are made up by the fake runtime below, so the images show what the dashboard renders and nothing
// anyone said. Everything listens on 127.0.0.1.
//
// Needs a Release build of the gateway and a dashboard build, and Chrome:
//
//   dotnet build -c Release
//   pnpm --dir web/AgentSplice.Dashboard build
//   node docs/images/capture.js        # CHROME=<path> if Chrome is not where it is looked for
'use strict';

const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { spawn, execFileSync } = require('child_process');

const repository = path.resolve(__dirname, '..', '..');
const gatewayPort = 5490;
const runtimePort = 1234;
const model = 'qwen3.6-35b-a3b';
const chrome = process.env.CHROME
  ?? (process.platform === 'win32' ? 'C:/Program Files/Google/Chrome/Application/chrome.exe' : 'google-chrome');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'agentsplice-capture-'));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const words = 'A trace proxy records when each phase of a request began and ended , and never what was said .'.split(' ');

// Answers as LM Studio 0.4.20 does in shape: headers at once for a stream, a pause for prompt
// processing, then deltas; a buffered answer only when it is finished.
const runtime = http.createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/v1/models') {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ object: 'list', data: [{ id: model, object: 'model', owned_by: 'organization_owner' }] }));
    return;
  }

  let body = '';
  for await (const chunk of request) body += chunk;
  const completion = JSON.parse(body);
  const id = 'chatcmpl-capture';
  const created = Math.floor(Date.now() / 1000);
  const event = (value) => 'data: ' + JSON.stringify(value) + '\n\n';
  const delta = (value, finish = null) => event({ id, object: 'chat.completion.chunk', created, model, choices: [{ index: 0, delta: value, finish_reason: finish }] });

  if (!completion.stream) {
    await sleep(1100);
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({
      id, object: 'chat.completion', created, model,
      choices: [{ index: 0, message: { role: 'assistant', content: words.join(' ') }, finish_reason: 'stop' }],
      usage: { prompt_tokens: 912, completion_tokens: words.length, total_tokens: 912 + words.length },
    }));
    return;
  }

  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  response.flushHeaders();

  if (JSON.stringify(completion.messages).includes('too long')) {
    // A runtime error inside a 200 stream, as ADR 0026 records it.
    await sleep(350);
    response.end(event({ error: { message: 'The prompt does not fit the loaded context.' } }));
    return;
  }

  await sleep(700);
  response.write(delta({ role: 'assistant', content: '' }));

  for (const word of words) {
    await sleep(45);
    response.write(delta({ content: word + ' ' }));
  }

  response.write(delta({}, 'stop'));
  response.write(event({ id, object: 'chat.completion.chunk', created, model, choices: [], usage: { prompt_tokens: 1843, completion_tokens: words.length, total_tokens: 1843 + words.length } }));
  response.end('data: [DONE]\n\n');
});

async function main() {
  await new Promise((resolve) => runtime.listen(runtimePort, '127.0.0.1', resolve));

  const bin = path.join(repository, 'src', 'AgentSplice.Api', 'bin', 'Release', 'net10.0');
  const gateway = spawn('dotnet', [
    path.join(bin, 'AgentSplice.Api.dll'),
    '--urls', `http://127.0.0.1:${gatewayPort}`,
    `--agentsplice:publicBaseUrl=http://127.0.0.1:${gatewayPort}`,
    `--agentsplice:runtimes:0:baseUrl=http://127.0.0.1:${runtimePort}/v1`,
    `--agentsplice:persistence:connectionString=Data Source=${path.join(scratch, 'agentsplice.db')}`,
    `--agentsplice:dashboard:rootPath=${path.join(repository, 'web', 'AgentSplice.Dashboard', 'dist')}`,
  ], { cwd: bin, stdio: 'ignore' });

  const base = `http://127.0.0.1:${gatewayPort}`;

  try {
    for (let attempt = 0; ; attempt++) {
      try { if ((await fetch(base + '/health/live')).status === 204) break; } catch { /* not yet */ }
      if (attempt > 100) throw new Error('The gateway did not start.');
      await sleep(200);
    }

    const complete = async (content, stream) => {
      const response = await fetch(base + '/v1/chat/completions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model,
          stream,
          ...(stream ? { stream_options: { include_usage: true } } : {}),
          messages: [{ role: 'system', content: 'You are a coding agent.' }, { role: 'user', content }],
        }),
      });
      await response.text();
      return response.headers.get('x-agentsplice-exchange-id');
    };

    await complete('Summarise the build log.', false);
    const streamed = await complete('Explain what a trace proxy records.', true);
    const failed = await complete('This prompt is too long for the context.', true);
    await complete('List the failing tests.', true);

    // The store is written in the background.
    await sleep(1500);

    const shoot = (name, route, height) => execFileSync(chrome, [
      '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--no-default-browser-check',
      `--user-data-dir=${path.join(scratch, 'chrome')}`,
      `--window-size=1280,${height}`, '--virtual-time-budget=8000',
      `--screenshot=${path.join(__dirname, name)}`, base + route,
    ], { stdio: 'ignore', timeout: 60000 });

    shoot('dashboard-overview.png', '/dashboard/', 660);
    shoot('dashboard-exchanges.png', '/dashboard/exchanges', 490);
    shoot('dashboard-exchange-stream.png', `/dashboard/exchanges/${streamed}`, 1470);
    shoot('dashboard-exchange-runtime-error.png', `/dashboard/exchanges/${failed}`, 1340);
  } finally {
    gateway.kill();
    runtime.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
