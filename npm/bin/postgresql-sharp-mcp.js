#!/usr/bin/env node
'use strict';

const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

function fail(message) {
  console.error(`postgresql-sharp-mcp: ${message}`);
  process.exitCode = 1;
}

const runtime = spawnSync('dotnet', ['--list-runtimes'], { encoding: 'utf8', windowsHide: true });
if (runtime.error || runtime.status !== 0 || !/^Microsoft\.NETCore\.App 10\./m.test(runtime.stdout || '')) {
  fail('The .NET 10 runtime is required. Install it for your platform and make dotnet available on PATH. This package never downloads a runtime.');
} else {
  const dll = path.join(__dirname, '..', 'payload', 'PostgreSqlMcp.dll');
  if (!fs.existsSync(dll)) {
    fail('The packaged application is missing. Reinstall from a complete package built with scripts/package.py.');
  } else {
    const child = spawn('dotnet', [dll, ...process.argv.slice(2)], { stdio: 'inherit', windowsHide: true });
    const handlers = new Map();
    for (const signal of ['SIGINT', 'SIGTERM', ...(process.platform === 'win32' ? [] : ['SIGHUP'])]) {
      const handler = () => child.kill(signal);
      handlers.set(signal, handler);
      process.on(signal, handler);
    }
    child.on('error', () => fail('Unable to launch dotnet. Check the .NET 10 runtime installation and PATH.'));
    child.on('close', (code, signal) => {
      for (const [name, handler] of handlers) process.removeListener(name, handler);
      if (signal && process.platform !== 'win32') {
        // Preserve signal termination, not just an arbitrary failure status.
        process.kill(process.pid, signal);
      } else {
        process.exitCode = code ?? (signal ? 128 + (os.constants.signals[signal] || 1) : 1);
      }
    });
  }
}
