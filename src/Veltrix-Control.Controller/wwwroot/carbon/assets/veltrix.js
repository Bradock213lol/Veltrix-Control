/* Veltrix-Control live bridge for the Carbon interface.
   Runs before app.js and provides real workspace data from the same-origin
   Controller API. When the bridge is absent (opening the kit standalone),
   app.js falls back to its bundled sample content. */
(function () {
  const api = async (path, options) => {
    const response = await fetch(path, Object.assign({ credentials: 'same-origin' }, options || {}));
    if (response.status === 401) return null;
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    return response.status === 204 ? null : response.json();
  };

  const gib = (bytes) => Math.round((bytes / (1024 * 1024 * 1024)) * 10) / 10;

  function mapDevice(device) {
    const telemetry = device.telemetry;
    const inventory = device.inventory || {};
    return {
      id: device.id,
      name: device.name,
      os: inventory.operatingSystem || 'Windows',
      ip: '',
      status: device.online ? 'Online' : 'Offline',
      cpu: telemetry ? Math.round(telemetry.cpuPercent) : 0,
      used: telemetry ? gib(telemetry.usedMemoryBytes) : 0,
      total: telemetry ? gib(telemetry.totalMemoryBytes) : 0,
      health: device.online ? device.healthScore : null,
      role: 'Managed node',
      uptime: telemetry ? formatUptime(telemetry.uptimeSeconds) : '-',
      lastSeen: device.lastHeartbeat
    };
  }

  function formatUptime(seconds) {
    const total = Math.max(0, seconds || 0);
    const days = Math.floor(total / 86400);
    const hours = Math.floor((total % 86400) / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    return days > 0 ? `${days}d ${String(hours).padStart(2, '0')}h` : `${hours}h ${String(minutes).padStart(2, '0')}m`;
  }

  function relativeTime(iso) {
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return '';
    const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
    if (seconds < 60) return 'Just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86400)}d ago`;
  }

  function mapAudit(events) {
    return events.slice(0, 6).map((event) => {
      const kind = /power|restart|shutdown/i.test(event.action) ? 'admin'
        : /file|transfer|backup/i.test(event.action) ? 'files'
          : /login|logout|user/i.test(event.action) ? 'lock' : 'check';
      return [kind, titleCase(event.action), `${event.actor} · ${event.target}`, relativeTime(event.timestamp)];
    });
  }

  function titleCase(value) {
    return String(value || '').replace(/[._]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  }

  window.VeltrixHost = {
    adapter: {
      async refresh() {
        const devices = await api('/api/devices');
        return (devices || []).map(mapDevice);
      },
      async createEnrollment() {
        const token = await api('/api/enrollment/tokens', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Veltrix-Control-Request': 'ui' },
          body: JSON.stringify({ lifetimeMinutes: 15 })
        });
        return { code: token ? token.code : '', expiresMinutes: 15 };
      }
    },
    devices: null,
    activity: null,
    loaded: false
  };

  window.VeltrixHost.ready = (async function load() {
    try {
      const me = await api('/api/auth/me');
      if (!me) {
        window.location.replace('/index.html');
        return;
      }
      const [devices, audit, info] = await Promise.all([
        api('/api/devices'),
        api('/api/audit?limit=6'),
        api('/api/controller/info').catch(() => null)
      ]);
      const mapped = (devices || []).map(mapDevice);
      window.VeltrixHost.devices = mapped;
      window.VeltrixHost.activity = mapAudit(audit || []);
      window.VeltrixHost.build = '0.10.7';
      window.VeltrixHost.workspaceName = 'Personal workspace';
      window.VeltrixHost.workspaceDetail = info && info.httpsPort ? `Controller · port ${info.httpsPort}` : 'Local controller';
      window.VeltrixHost.endpoint = window.location.host;
      window.VeltrixHost.user = {
        initials: initials(me.username),
        name: me.username,
        role: me.role
      };
      window.VeltrixHost.loaded = true;
    } catch (error) {
      console.error('Veltrix bridge failed', error);
    }
  })();

  function initials(username) {
    const parts = String(username || '').split(/[._\- ]+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return String(username || '?').slice(0, 2).toUpperCase();
  }
})();
