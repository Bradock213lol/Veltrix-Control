const state = { setup: false, user: null, devices: [], activeView: 'overview', refreshTimer: null, socket: null };
const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const auth = $('#auth');
const shell = $('#shell');
const modal = $('#modal');
const modalContent = $('#modal-content');

async function request(path, options = {}) {
  const method = (options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  if (options.body && typeof options.body !== 'string') {
    headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(options.body);
  }
  if (!['GET', 'HEAD'].includes(method)) headers['X-NexaGrid-Request'] = 'ui';
  const response = await fetch(path, { credentials: 'same-origin', ...options, method, headers });
  if (response.status === 401 && !path.includes('/auth/login') && !path.includes('/agent/')) showAuth(false);
  const contentType = response.headers.get('content-type') || '';
  const body = contentType.includes('json') ? await response.json() : null;
  if (!response.ok) throw new Error(body?.error || `${response.status} ${response.statusText}`);
  return body;
}

async function initialize() {
  try {
    const setup = await request('/api/setup/status');
    if (setup.required) return showAuth(true);
    state.user = await request('/api/auth/me');
    showShell();
  } catch {
    showAuth(false);
  }
}

function showAuth(isSetup) {
  state.setup = isSetup;
  shell.hidden = true;
  auth.hidden = false;
  $('#auth-title').textContent = isSetup ? 'Create your control plane' : 'Welcome back';
  $('#auth-copy').textContent = isSetup ? 'Set the first Owner account. Use at least 12 characters.' : 'Sign in to your authorized fleet.';
  $('#auth-action').textContent = isSetup ? 'Initialize controller' : 'Sign in';
  $('#password').autocomplete = isSetup ? 'new-password' : 'current-password';
}

async function showShell() {
  auth.hidden = true;
  shell.hidden = false;
  $('#user-name').textContent = state.user.username;
  $('#user-role').textContent = state.user.role;
  $('#user-initial').textContent = state.user.username[0].toUpperCase();
  await refreshDevices();
  $('#loading').hidden = true;
  $('#content').hidden = false;
  connectLiveUpdates();
  clearInterval(state.refreshTimer);
  state.refreshTimer = setInterval(refreshDevices, 5000);
}

$('#auth-form').addEventListener('submit', async event => {
  event.preventDefault();
  $('#auth-error').textContent = '';
  const body = { username: $('#username').value.trim(), password: $('#password').value };
  try {
    state.user = await request(state.setup ? '/api/setup' : '/api/auth/login', { method: 'POST', body });
    await showShell();
  } catch (error) {
    $('#auth-error').textContent = error.message === '401 Unauthorized' ? 'The username or password is incorrect.' : error.message;
  }
});

$('#logout').addEventListener('click', async () => {
  try { await request('/api/auth/logout', { method: 'POST' }); } catch { /* session is gone either way */ }
  clearInterval(state.refreshTimer);
  state.socket?.close();
  showAuth(false);
});

async function refreshDevices() {
  if (!state.user) return;
  try {
    state.devices = await request('/api/devices');
    renderOverview();
    renderDeviceTable();
    $('#live-dot').className = 'status-dot online';
    $('#live-label').textContent = 'Live';
  } catch (error) {
    $('#live-dot').className = 'status-dot offline';
    $('#live-label').textContent = 'Reconnecting';
    if (!error.message.includes('Unauthorized')) toast(error.message, true);
  }
}

function renderOverview() {
  const online = state.devices.filter(device => device.online);
  const cpu = online.length ? online.reduce((sum, device) => sum + (device.telemetry?.cpuPercent || 0), 0) / online.length : 0;
  const usedMemory = online.reduce((sum, device) => sum + (device.telemetry?.usedMemoryBytes || 0), 0);
  $('#online-count').textContent = online.length;
  $('#online-caption').textContent = `${state.devices.length - online.length} offline · ${state.devices.length} enrolled`;
  $('#cpu-average').textContent = `${cpu.toFixed(0)}%`;
  $('#memory-total').textContent = formatBytes(usedMemory);
  $('#memory-caption').textContent = `Across ${online.length} live node${online.length === 1 ? '' : 's'}`;
  $('#attention-count').textContent = state.devices.filter(device => device.healthScore < 75).length;
  $('#nav-device-count').textContent = state.devices.length;
  $('#last-sync').textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  $('#device-grid').innerHTML = state.devices.length
    ? state.devices.map(deviceCard).join('')
    : '<div class="empty-device"><b>No devices enrolled</b><p>Create a one-time enrollment code to connect your first authorized node.</p></div>';
}

function deviceCard(device) {
  const memory = device.telemetry?.totalMemoryBytes ? device.telemetry.usedMemoryBytes * 100 / device.telemetry.totalMemoryBytes : 0;
  const cpu = device.telemetry?.cpuPercent || 0;
  return `<article class="device-card glass ${device.online ? '' : 'device-offline'}" data-device-id="${device.id}" tabindex="0">
    <div class="device-head"><div class="device-name"><span class="device-glyph">${device.inventory.isSimulation ? 'S' : '▣'}</span><div><b>${escapeHtml(device.name)}</b><small><span class="status-dot ${device.online ? 'online' : 'offline'}"></span> ${device.online ? ' Online' : ' Offline'} · ${escapeHtml(device.inventory.operatingSystem)}</small></div></div><div class="health-ring" style="--health:${device.healthScore}"><span>${device.healthScore}</span></div></div>
    <div class="telemetry"><div><div class="telemetry-label"><span>CPU</span><b>${cpu.toFixed(0)}%</b></div><div class="bar"><i style="width:${cpu}%"></i></div></div><div><div class="telemetry-label"><span>RAM</span><b>${memory.toFixed(0)}%</b></div><div class="bar"><i style="width:${memory}%"></i></div></div></div>
    <div class="device-foot"><span>${device.inventory.logicalProcessors} logical CPUs</span><span>${formatBytes(device.inventory.totalMemoryBytes)} RAM</span><span>${device.inventory.isSimulation ? 'SIMULATED' : `v${escapeHtml(device.inventory.agentVersion)}`}</span></div>
  </article>`;
}

function renderDeviceTable() {
  const query = ($('#device-search')?.value || '').toLowerCase();
  const devices = state.devices.filter(device => device.name.toLowerCase().includes(query));
  $('#device-table').innerHTML = `<div class="device-row row-header"><span>DEVICE</span><span>STATUS</span><span>CPU</span><span>MEMORY</span><span>HEALTH</span></div>` +
    devices.map(device => `<div class="device-row" data-device-id="${device.id}"><b>${escapeHtml(device.name)}</b><span class="pill ${device.online ? '' : 'offline'}"><span class="status-dot ${device.online ? 'online' : 'offline'}"></span>${device.online ? 'Online' : 'Offline'}</span><span>${(device.telemetry?.cpuPercent || 0).toFixed(0)}%</span><span>${formatBytes(device.telemetry?.usedMemoryBytes || 0)}</span><b>${device.healthScore}</b></div>`).join('');
}

$('#device-search').addEventListener('input', renderDeviceTable);
document.addEventListener('click', event => {
  const card = event.target.closest('[data-device-id]');
  if (card && !event.target.closest('button')) openDevice(card.dataset.deviceId);
});
document.addEventListener('keydown', event => {
  if ((event.key === 'Enter' || event.key === ' ') && event.target.matches('.device-card')) openDevice(event.target.dataset.deviceId);
});

function openDevice(id) {
  const device = state.devices.find(item => item.id === id);
  if (!device) return;
  modalContent.innerHTML = `<p class="eyebrow">${device.inventory.isSimulation ? 'SIMULATED NODE' : 'MANAGED NODE'}</p><h2>${escapeHtml(device.name)}</h2><p>${escapeHtml(device.inventory.operatingSystem)} · ${device.inventory.logicalProcessors} logical CPUs · ${formatBytes(device.inventory.totalMemoryBytes)} RAM</p>
    <div class="device-actions"><button class="secondary" data-modal-action="files" data-id="${device.id}">Browse files</button><button class="secondary" data-modal-action="restart" data-id="${device.id}">Restart</button><button class="danger-button" data-modal-action="shutdown" data-id="${device.id}">Shut down</button></div>
    <p><small>Last heartbeat ${new Date(device.lastHeartbeat).toLocaleString()}. Power actions also require this node’s local policy switch.</small></p>`;
  modal.showModal();
}

modal.addEventListener('click', event => {
  if (event.target === modal) modal.close();
  const button = event.target.closest('[data-modal-action]');
  if (!button) return;
  const action = button.dataset.modalAction;
  if (action === 'files') browseFiles(button.dataset.id, '');
  if (action === 'restart' || action === 'shutdown') confirmPower(button.dataset.id, action);
  if (action === 'confirm-power') queueOperation(button.dataset.id, button.dataset.kind, null, true);
});

function confirmPower(id, kind) {
  const device = state.devices.find(item => item.id === id);
  modalContent.innerHTML = `<p class="eyebrow">CONFIRM HIGH-IMPACT ACTION</p><h2>${kind === 'restart' ? 'Restart' : 'Shut down'} ${escapeHtml(device?.name || 'device')}?</h2><p>This request will be signed, queued, and recorded in the audit log. The node must explicitly allow local power actions.</p><div class="modal-actions"><button class="secondary" data-close-modal>Cancel</button><button class="danger-button" data-modal-action="confirm-power" data-id="${id}" data-kind="${kind}">Confirm ${kind}</button></div>`;
  $('[data-close-modal]').addEventListener('click', () => modal.close(), { once: true });
}

async function queueOperation(deviceId, kind, argument, confirmed) {
  try {
    const operation = await request(`/api/devices/${deviceId}/operations`, { method: 'POST', body: { kind, argument, confirmed } });
    toast(`${kind} queued with operation ${operation.id.slice(0, 8)}.`);
    if (kind === 'ListDirectory') await waitForFiles(operation.id, deviceId, argument || '');
    else modal.close();
  } catch (error) { toast(error.message, true); }
}

async function browseFiles(deviceId, relativePath) {
  modalContent.innerHTML = '<p class="eyebrow">REMOTE FILES</p><h2>Loading managed root…</h2><p>The request is being executed by the authorized node.</p>';
  await queueOperation(deviceId, 'ListDirectory', relativePath, true);
}

async function waitForFiles(operationId, deviceId, path) {
  for (let attempt = 0; attempt < 40; attempt++) {
    await delay(750);
    const operation = await request(`/api/operations/${operationId}`);
    if (operation.state === 'Failed') throw new Error(operation.error || 'File request failed.');
    if (operation.state === 'Succeeded') {
      const files = JSON.parse(operation.resultJson || '[]');
      modalContent.innerHTML = `<p class="eyebrow">REMOTE FILES</p><h2>${escapeHtml(path || 'Managed root')}</h2><p>${files.length} entries returned by the node.</p><div class="file-list">${files.length ? files.map(file => `<div class="file-row"><span>${file.isDirectory ? '▱' : '·'}</span><b>${escapeHtml(file.name)}</b><small>${file.isDirectory ? 'Folder' : formatBytes(file.sizeBytes || 0)}</small></div>`).join('') : '<p>This folder is empty.</p>'}</div>`;
      return;
    }
  }
  throw new Error('The node did not complete the file request in time.');
}

$('#add-device').addEventListener('click', async () => {
  try {
    const [token, info] = await Promise.all([
      request('/api/enrollment/tokens', { method: 'POST', body: { lifetimeMinutes: 15 } }),
      request('/api/controller/info')
    ]);
    const controllerUrl = `https://${location.hostname}:${info.httpsPort}`;
    modalContent.innerHTML = `<p class="eyebrow">SECURE ENROLLMENT</p><h2>Connect a managed node</h2><p>Use these values in the node installer. The single-use code expires ${new Date(token.expiresAt).toLocaleTimeString()}.</p><small>CONTROLLER URL</small><div class="token-box"><code>${escapeHtml(controllerUrl)}</code></div><small>CERTIFICATE SHA-256</small><div class="token-box"><code>${escapeHtml(info.certificateSha256 || 'Available on HTTPS-enabled builds')}</code></div><small>ENROLLMENT CODE</small><div class="token-box"><code>${escapeHtml(token.code)}</code><button id="copy-token" class="secondary">Copy code</button></div><p><small>Verify the fingerprint on the Controller host before sharing it. The node creates its own ECDSA identity key; NexaGrid stores only the public key.</small></p>`;
    modal.showModal();
    $('#copy-token').addEventListener('click', async () => { await navigator.clipboard.writeText(token.code); toast('Enrollment code copied.'); });
  } catch (error) { toast(error.message, true); }
});

$('#files-select').addEventListener('click', () => {
  modalContent.innerHTML = `<p class="eyebrow">CHOOSE NODE</p><h2>Browse managed files</h2><div class="file-list">${state.devices.filter(d => d.online).map(d => `<button class="nav-item" data-modal-action="files" data-id="${d.id}"><span class="status-dot online"></span><b>${escapeHtml(d.name)}</b></button>`).join('') || '<p>No nodes are online.</p>'}</div>`;
  modal.showModal();
});

async function loadAudit() {
  try {
    const [events, integrity] = await Promise.all([request('/api/audit?limit=200'), request('/api/audit/integrity')]);
    $('#audit-integrity').textContent = integrity.valid ? '✓ Chain verified' : 'Chain verification failed';
    $('#audit-integrity').className = `integrity ${integrity.valid ? '' : 'bad'}`;
    $('#audit-table').innerHTML = '<div class="audit-row row-header"><span>TIME</span><span>ACTOR</span><span>ACTION</span><span>TARGET</span><span>OUTCOME</span></div>' + events.map(entry => `<div class="audit-row"><span>${new Date(entry.timestamp).toLocaleString()}</span><b>${escapeHtml(entry.actor)}</b><span>${escapeHtml(entry.action)}</span><span>${escapeHtml(entry.target)}</span><span>${escapeHtml(entry.outcome)}</span></div>`).join('');
  } catch (error) { toast(error.message, true); }
}

$('#nav').addEventListener('click', event => {
  const item = event.target.closest('[data-view]');
  if (!item) return;
  state.activeView = item.dataset.view;
  $$('.nav-item').forEach(button => button.classList.toggle('active', button === item));
  $$('.view').forEach(view => view.classList.toggle('active', view.id === `${state.activeView}-view`));
  const labels = { overview: ['FLEET OVERVIEW', 'Command center'], devices: ['AUTHORIZED NODES', 'Device inventory'], files: ['REMOTE FILES', 'Managed storage'], audit: ['SECURITY & COMPLIANCE', 'Audit history'] };
  $('#breadcrumb').textContent = labels[state.activeView][0];
  $('#view-title').textContent = labels[state.activeView][1];
  if (state.activeView === 'audit') loadAudit();
});

async function connectLiveUpdates() {
  try {
    const negotiation = await request('/hubs/fleet/negotiate?negotiateVersion=1', { method: 'POST' });
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${scheme}//${location.host}/hubs/fleet?id=${encodeURIComponent(negotiation.connectionToken)}`);
    state.socket = socket;
    socket.addEventListener('open', () => socket.send('{"protocol":"json","version":1}\u001e'));
    socket.addEventListener('message', event => {
      for (const frame of event.data.split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        if (message.type === 1 && ['deviceUpdated', 'operationUpdated'].includes(message.target)) refreshDevices();
      }
    });
    socket.addEventListener('close', () => { if (state.user) setTimeout(connectLiveUpdates, 3000); });
  } catch { if (state.user) setTimeout(connectLiveUpdates, 3000); }
}

function formatBytes(bytes) {
  if (!bytes) return '0 GB';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** index).toFixed(index >= 3 ? 1 : 0)} ${units[index]}`;
}
function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[char]); }
function delay(milliseconds) { return new Promise(resolve => setTimeout(resolve, milliseconds)); }
function toast(message, error = false) { const element = $('#toast'); element.textContent = message; element.className = `toast show ${error ? 'error' : ''}`; clearTimeout(toast.timer); toast.timer = setTimeout(() => element.className = 'toast', 4000); }

initialize();
