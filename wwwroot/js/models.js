// models.js - Model management logic (Foundry Local only)
const modelsTableBody = document.getElementById('models-table-body');
const btnRefresh = document.getElementById('btn-refresh');
const btnDownloadSelected = document.getElementById('btn-download-selected');
const selectedCountSpan = document.getElementById('selected-count');
const selectAllCheckbox = document.getElementById('select-all');
const downloadProgress = document.getElementById('download-progress');
const downloadBar = document.getElementById('download-bar');
const downloadStatus = document.getElementById('download-status');
const downloadModelName = document.getElementById('download-model-name');

let allModels = [];
let systemRamMb = null;

function formatSize(bytes) {
    if (!bytes) return '—';
    const gb = bytes / (1024 * 1024 * 1024);
    if (gb >= 1) return `${gb.toFixed(1)} GB`;
    const mb = bytes / (1024 * 1024);
    return `${mb.toFixed(0)} MB`;
}

function formatRam(mb) {
    if (!mb) return '—';
    if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
    return `${Math.round(mb)} MB`;
}

function canRunBadge(estimatedRamMb) {
    if (!estimatedRamMb || !systemRamMb) return '<span class="badge bg-secondary">❓ Unknown</span>';
    const ratio = estimatedRamMb / systemRamMb;
    if (ratio <= 0.5) return '<span class="badge bg-success" title="Comfortable — uses less than 50% of RAM">✅ Yes</span>';
    if (ratio <= 0.75) return '<span class="badge bg-warning text-dark" title="Tight — uses 50-75% of RAM">⚠️ Tight</span>';
    return '<span class="badge bg-danger" title="Model likely too large for available RAM">❌ No</span>';
}

function statusBadge(status) {
    const map = {
        'loaded': 'bg-success',
        'downloaded': 'bg-info',
        'available': 'bg-secondary'
    };
    const labels = {
        'loaded': '🟢 Loaded',
        'downloaded': '🔵 Downloaded',
        'available': '⬜ Available'
    };
    return `<span class="badge ${map[status] || 'bg-secondary'}">${labels[status] || status || 'unknown'}</span>`;
}

async function loadModels() {
    try {
        // Fetch system info and models in parallel
        const [sysRes, modelsRes] = await Promise.all([
            fetch('/api/system-info'),
            fetch('/api/models?provider=foundry')
        ]);
        if (sysRes.ok) {
            const sysInfo = await sysRes.json();
            systemRamMb = sysInfo.totalRamMb;
        }
        allModels = await modelsRes.json();
        renderModels();
    } catch (err) {
        modelsTableBody.innerHTML = `<tr><td colspan="8" class="text-center text-danger">Error loading models: ${err.message}</td></tr>`;
    }
}

function renderModels() {
    if (allModels.length === 0) {
        modelsTableBody.innerHTML = '<tr><td colspan="8" class="text-center text-muted py-4">No models found. Check Foundry Local connection.</td></tr>';
        return;
    }

    // Sort: loaded first, then downloaded, then available
    const order = { 'loaded': 0, 'downloaded': 1, 'available': 2 };
    const sorted = [...allModels].sort((a, b) => (order[a.status] ?? 3) - (order[b.status] ?? 3));

    modelsTableBody.innerHTML = sorted.map(m => {
        const isAvailable = m.status === 'available';
        const checkboxId = `chk-${(m.id || '').replace(/[^a-zA-Z0-9]/g, '-')}`;
        return `
        <tr>
            <td>
                ${isAvailable
                    ? `<input type="checkbox" class="form-check-input model-checkbox" data-model-id="${m.id}" id="${checkboxId}" />`
                    : ''}
            </td>
            <td>
                <strong>${m.name || m.id}</strong>
                ${m.description ? `<br><small class="text-muted">${m.description}</small>` : ''}
                ${m.family ? `<br><span class="badge bg-dark">${m.family}</span>` : ''}
            </td>
            <td>${statusBadge(m.status)}</td>
            <td>${formatSize(m.size)}</td>
            <td>${formatRam(m.estimatedRamMb)}</td>
            <td>${canRunBadge(m.estimatedRamMb)}</td>
            <td>${m.parameterSize || '—'}</td>
            <td>
                ${isAvailable ? `<button class="btn btn-sm btn-outline-primary" onclick="downloadModel('${m.id}')">⬇️ Download</button>` : ''}
                ${m.status === 'downloaded' || m.status === 'loaded' ? `<a href="/" class="btn btn-sm btn-outline-success">💬 Chat</a>` : ''}
            </td>
        </tr>`;
    }).join('');

    // Attach checkbox listeners
    document.querySelectorAll('.model-checkbox').forEach(cb => {
        cb.addEventListener('change', updateSelectedCount);
    });
    updateSelectedCount();
}

function getSelectedModelIds() {
    return Array.from(document.querySelectorAll('.model-checkbox:checked')).map(cb => cb.dataset.modelId);
}

function updateSelectedCount() {
    const count = getSelectedModelIds().length;
    selectedCountSpan.textContent = count;
    btnDownloadSelected.disabled = count === 0;

    // Update select-all state
    const allCheckboxes = document.querySelectorAll('.model-checkbox');
    if (allCheckboxes.length === 0) {
        selectAllCheckbox.checked = false;
        selectAllCheckbox.indeterminate = false;
    } else if (count === allCheckboxes.length) {
        selectAllCheckbox.checked = true;
        selectAllCheckbox.indeterminate = false;
    } else if (count > 0) {
        selectAllCheckbox.checked = false;
        selectAllCheckbox.indeterminate = true;
    } else {
        selectAllCheckbox.checked = false;
        selectAllCheckbox.indeterminate = false;
    }
}

// Select all toggle
selectAllCheckbox.addEventListener('change', () => {
    const checked = selectAllCheckbox.checked;
    document.querySelectorAll('.model-checkbox').forEach(cb => { cb.checked = checked; });
    updateSelectedCount();
});

// Download a single model
async function downloadModel(modelId) {
    await startDownload(modelId);
}

// Download selected models sequentially
async function downloadSelected() {
    const ids = getSelectedModelIds();
    if (ids.length === 0) return;

    btnDownloadSelected.disabled = true;

    for (let i = 0; i < ids.length; i++) {
        downloadModelName.textContent = `Downloading ${ids[i]} (${i + 1} of ${ids.length})...`;
        await startDownload(ids[i]);
    }

    btnDownloadSelected.disabled = false;
    updateSelectedCount();
}

async function startDownload(modelId) {
    downloadProgress.classList.remove('d-none');
    downloadBar.style.width = '0%';
    downloadBar.textContent = '0%';
    downloadBar.classList.remove('bg-danger');
    downloadBar.classList.add('progress-bar-animated');
    downloadStatus.textContent = 'Starting download...';
    downloadModelName.textContent = `Downloading ${modelId}...`;

    return new Promise(async (resolve) => {
        try {
            const res = await fetch('/api/models/download', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ modelId, provider: 'foundry' })
            });

            const reader = res.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            while (true) {
                const { done, value } = await reader.read();
                if (done) break;

                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop() || '';

                for (const line of lines) {
                    if (line.startsWith('data: ')) {
                        try {
                            const data = JSON.parse(line.substring(6));
                            if (data.percent != null) {
                                downloadBar.style.width = `${data.percent}%`;
                                downloadBar.textContent = `${Math.round(data.percent)}%`;
                            }
                            downloadStatus.textContent = data.status || '';

                            if (data.status === 'complete' || data.status === 'success') {
                                downloadBar.style.width = '100%';
                                downloadBar.textContent = '100%';
                                downloadBar.classList.remove('progress-bar-animated');
                                downloadStatus.textContent = `✅ ${modelId} downloaded!`;
                            }
                            if (data.status && data.status.startsWith('error')) {
                                downloadBar.classList.add('bg-danger');
                                downloadStatus.textContent = `❌ Failed: ${data.status}`;
                            }
                        } catch { }
                    }
                }
            }
        } catch (err) {
            downloadStatus.textContent = `❌ Error: ${err.message}`;
            downloadBar.classList.add('bg-danger');
        }

        // Refresh model list and uncheck the downloaded model
        await loadModels();
        resolve();
    });
}

// Event listeners
btnRefresh.addEventListener('click', loadModels);
btnDownloadSelected.addEventListener('click', downloadSelected);

// Make downloadModel available globally for inline onclick
window.downloadModel = downloadModel;

// Init
loadModels();
