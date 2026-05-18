let editor;
let executorId = null;
let leaseTtlSeconds = 120;
let leaseTimer = null;
let events = null;

const templates = {
  cpp: `#include <iostream>\nusing namespace std;\n\nint main() {\n    cout << "Hello CloudLiteIDE" << endl;\n    return 0;\n}\n`,
  python: `print("Hello CloudLiteIDE")\n`
};

const statusBox = document.getElementById('status');
const compileBox = document.getElementById('compile');
const stdoutBox = document.getElementById('stdout');
const stderrBox = document.getElementById('stderr');
const languageSelect = document.getElementById('language');

function appendLine(target, message) {
  target.textContent += `${message}\n`;
  target.scrollTop = target.scrollHeight;
}

function clearOutput() {
  statusBox.textContent = '';
  compileBox.textContent = '';
  stdoutBox.textContent = '';
  stderrBox.textContent = '';
}

function setDefaultTemplate(language) {
  const current = editor.getValue().trim();
  if (!current) {
    editor.setValue(templates[language]);
  }
}

async function ensureExecutor() {
  if (executorId) return;
  const res = await fetch('/api/executors', { method: 'POST' });
  if (!res.ok) throw new Error('Failed to allocate executor');
  const data = await res.json();
  executorId = data.executorId;
  leaseTtlSeconds = data.leaseTtlSeconds;
  openSse();
  startKeepAlive();
}

function openSse() {
  if (events) events.close();
  events = new EventSource(`/api/executors/${executorId}/events`);
  events.onmessage = (event) => {
    const payload = JSON.parse(event.data);
    handleEvent(payload);
  };
  events.addEventListener('Status', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('CompileStdout', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('CompileStderr', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('RuntimeStdout', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('RuntimeStderr', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('Error', (event) => handleEvent(JSON.parse(event.data)));
  events.addEventListener('Result', (event) => handleEvent(JSON.parse(event.data)));
}

function handleEvent(evt) {
  switch (evt.type) {
    case 'Status':
      appendLine(statusBox, evt.message);
      break;
    case 'CompileStdout':
    case 'CompileStderr':
      appendLine(compileBox, evt.message);
      break;
    case 'RuntimeStdout':
      appendLine(stdoutBox, evt.message);
      break;
    case 'RuntimeStderr':
      appendLine(stderrBox, evt.message);
      break;
    case 'Error':
      appendLine(stderrBox, evt.message);
      break;
    case 'Result':
      appendLine(statusBox, evt.message);
      break;
    default:
      appendLine(statusBox, `${evt.type}: ${evt.message}`);
      break;
  }
}

function startKeepAlive() {
  if (leaseTimer) clearInterval(leaseTimer);
  leaseTimer = setInterval(async () => {
    if (!executorId) return;
    const res = await fetch(`/api/executors/${executorId}/renew`, { method: 'POST' });
    if (!res.ok) {
      appendLine(statusBox, 'Executor expired. Recreating...');
      executorId = null;
      await ensureExecutor();
    }
  }, Math.max(5000, (leaseTtlSeconds * 1000) / 2));
}

async function execute() {
  await ensureExecutor();
  const language = languageSelect.value;
  const payload = {
    language,
    code: editor.getValue(),
    stdin: document.getElementById('stdin').value,
    cppOptions: {
      compiler: document.getElementById('compiler').value,
      standard: document.getElementById('standard').value,
      optimization: document.getElementById('optimization').value,
      warningLevel: document.getElementById('warnings').value
    }
  };

  const res = await fetch(`/api/executors/${executorId}/execute`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: 'Execution request failed' }));
    appendLine(stderrBox, err.error || 'Execution request failed');
  }
}

document.getElementById('runBtn').addEventListener('click', execute);
document.getElementById('clearBtn').addEventListener('click', clearOutput);
languageSelect.addEventListener('change', () => {
  const language = languageSelect.value;
  monaco.editor.setModelLanguage(editor.getModel(), language === 'cpp' ? 'cpp' : 'python');
  setDefaultTemplate(language);
});

require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' } });
require(['vs/editor/editor.main'], function () {
  editor = monaco.editor.create(document.getElementById('editor'), {
    value: templates.cpp,
    language: 'cpp',
    automaticLayout: true,
    minimap: { enabled: false },
    theme: 'vs-dark'
  });
});

ensureExecutor().catch((err) => appendLine(stderrBox, err.message));
