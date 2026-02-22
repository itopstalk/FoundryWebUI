// logs.js - Logs page: fetch and display logs from multiple sources

let currentSource = 'app';
let autoScroll = true;

async function loadLogs(source) {
    currentSource = source || currentSource;
    const viewer = document.getElementById('log-viewer');
    const showing = document.getElementById('log-showing');

    try {
        const res = await fetch(`/api/logs/${currentSource}?lines=500`);
        if (!res.ok) {
            viewer.innerHTML = `<div class="text-danger p-3">Error loading logs: HTTP ${res.status}</div>`;
            return;
        }
        const data = await res.json();
        const entries = data.entries || [];

        // Update count badge
        const badge = document.getElementById(`${currentSource}-count`);
        if (badge) badge.textContent = entries.length;

        // Apply filters
        const levelFilter = document.getElementById('log-level-filter').value;
        const searchText = document.getElementById('log-search').value.toLowerCase();

        const filtered = entries.filter(e => {
            // Level filter
            if (levelFilter !== 'all') {
                const entryLevel = (e.level || '').toLowerCase();
                const levels = { 'error': 0, 'critical': 0, 'warn': 1, 'warning': 1, 'info': 2, 'information': 2, 'debug': 3, 'trace': 4 };
                const filterLevel = levels[levelFilter] ?? 2;
                const eLevel = levels[entryLevel] ?? 2;
                if (eLevel > filterLevel) return false;
            }
            // Text search
            if (searchText) {
                const text = JSON.stringify(e).toLowerCase();
                if (!text.includes(searchText)) return false;
            }
            return true;
        });

        if (showing) showing.textContent = `Showing ${filtered.length} of ${entries.length} entries`;

        if (filtered.length === 0) {
            viewer.innerHTML = `<div class="text-muted p-3">${entries.length === 0 ? 'No log entries found.' : 'No entries match the current filter.'}</div>`;
            if (data.logDir) {
                viewer.innerHTML += `<div class="text-muted small p-3">Log directory: ${data.logDir}</div>`;
            }
            return;
        }

        // Render log lines
        const html = filtered.map(e => {
            const level = (e.level || '').toLowerCase();
            let cssClass = '';
            if (level === 'error' || level === 'critical') cssClass = 'log-line-error';
            else if (level === 'warn' || level === 'warning') cssClass = 'log-line-warn';
            else if (level === 'info' || level === 'information') cssClass = 'log-line-info';
            else if (level === 'debug' || level === 'trace') cssClass = 'log-line-debug';

            if (currentSource === 'app') {
                const cat = e.category ? `[${e.category}]` : '';
                return `<div class="${cssClass}">${esc(e.time)} ${esc(level.toUpperCase().padEnd(5))} ${esc(cat)} ${esc(e.message)}</div>`;
            } else if (currentSource === 'iis') {
                const file = e.file ? `[${e.file}] ` : '';
                return `<div>${esc(file)}${esc(e.message)}</div>`;
            } else if (currentSource === 'foundry') {
                const file = e.file ? `[${e.file}] ` : '';
                return `<div>${esc(file)}${esc(e.message)}</div>`;
            } else if (currentSource === 'eventlog') {
                return `<div class="${cssClass}">${esc(e.time)} [${esc(e.source)}] ${esc(level.toUpperCase().padEnd(5))} ${esc(e.message)}</div>`;
            }
            return `<div>${esc(JSON.stringify(e))}</div>`;
        }).join('');

        viewer.innerHTML = html;

        // Show log directory info if available
        if (data.logDir && data.logDir !== '(not found)') {
            viewer.innerHTML = `<div class="text-muted small mb-2" style="opacity:0.6;">📁 ${esc(data.logDir)}</div>` + viewer.innerHTML;
        }

        if (autoScroll) {
            viewer.scrollTop = viewer.scrollHeight;
        }

    } catch (err) {
        viewer.innerHTML = `<div class="text-danger p-3">Error: ${esc(err.message)}</div>`;
    }
}

function esc(text) {
    if (!text) return '';
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

document.addEventListener('DOMContentLoaded', () => {
    // Tab switching
    document.querySelectorAll('.nav-tab-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.nav-tab-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            loadLogs(btn.dataset.source);
        });
    });

    // Refresh
    document.getElementById('btn-refresh-logs')?.addEventListener('click', () => loadLogs());

    // Auto-scroll toggle
    const autoScrollBtn = document.getElementById('btn-auto-scroll');
    autoScrollBtn?.addEventListener('click', () => {
        autoScroll = !autoScroll;
        autoScrollBtn.classList.toggle('active', autoScroll);
    });

    // Filters
    document.getElementById('log-level-filter')?.addEventListener('change', () => loadLogs());
    let searchTimeout;
    document.getElementById('log-search')?.addEventListener('input', () => {
        clearTimeout(searchTimeout);
        searchTimeout = setTimeout(() => loadLogs(), 300);
    });

    // Initial load + preload counts for all tabs
    loadLogs('app');
    ['iis', 'foundry', 'eventlog'].forEach(async source => {
        try {
            const res = await fetch(`/api/logs/${source}?lines=500`);
            if (res.ok) {
                const data = await res.json();
                const badge = document.getElementById(`${source}-count`);
                if (badge) badge.textContent = (data.entries || []).length;
            }
        } catch {}
    });

    // Auto-refresh every 10 seconds
    setInterval(() => loadLogs(), 10000);
});
