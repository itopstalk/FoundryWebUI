// site.js - Global: check provider status on load
(async function () {
    try {
        const res = await fetch('/api/status');
        const statuses = await res.json();
        const container = document.getElementById('provider-status');
        if (!container) return;
        container.innerHTML = statuses.map(s =>
            `<span class="badge ${s.isAvailable ? 'bg-success' : 'bg-danger'}" title="${s.error || s.endpoint || ''}">
                ${s.provider} ${s.isAvailable ? '✓' : '✗'}
            </span>`
        ).join('');
    } catch { }
})();
