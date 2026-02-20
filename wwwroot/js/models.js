// models.js - Model management logic
const modelsTableBody = document.getElementById('models-table-body');
const providerFilter = document.getElementById('provider-filter');
const btnRefresh = document.getElementById('btn-refresh');
const btnDownload = document.getElementById('btn-download');
const downloadProvider = document.getElementById('download-provider');
const downloadModelId = document.getElementById('download-model-id');
const downloadProgress = document.getElementById('download-progress');
const downloadBar = document.getElementById('download-bar');
const downloadStatus = document.getElementById('download-status');

let allModels = [];

function formatSize(bytes) {
    if (!bytes) return '—';
    const gb = bytes / (1024 * 1024 * 1024);
    if (gb >= 1) return `${gb.toFixed(1)} GB`;
    const mb = bytes / (1024 * 1024);
    return `${mb.toFixed(0)} MB`;
}

function statusBadge(status) {
    const map = {
        'loaded': 'bg-success',
        'downloaded': 'bg-info',
        'available': 'bg-secondary'
    };
    return `<span class="badge ${map[status] || 'bg-secondary'}">${status || 'unknown'}</span>`;
}

function providerBadge(provider) {
    return `<span class="badge ${provider === 'foundry' ? 'bg-info' : 'bg-warning'}">${provider}</span>`;
}

async function loadModels() {
    try {
        const filter = providerFilter.value;
        const url = filter ? `/api/models?provider=${filter}` : '/api/models';
        const res = await fetch(url);
        allModels = await res.json();
        renderModels();
    } catch (err) {
        modelsTableBody.innerHTML = `<tr><td colspan="6" class="text-center text-danger">Error loading models: ${err.message}</td></tr>`;
    }
}

function renderModels() {
    if (allModels.length === 0) {
        modelsTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted py-4">No models found</td></tr>';
        return;
    }

    modelsTableBody.innerHTML = allModels.map(m => `
        <tr>
            <td>
                <strong>${m.name || m.id}</strong>
                ${m.description ? `<br><small class="text-muted">${m.description}</small>` : ''}
                ${m.family ? `<br><span class="badge bg-dark">${m.family}</span>` : ''}
            </td>
            <td>${providerBadge(m.provider)}</td>
            <td>${statusBadge(m.status)}</td>
            <td>${formatSize(m.size)}</td>
            <td>${m.parameterSize || '—'}</td>
            <td>
                ${m.status === 'available' ? `<button class="btn btn-sm btn-outline-primary" onclick="downloadModel('${m.id}', '${m.provider}')">⬇️ Download</button>` : ''}
                ${m.status === 'downloaded' || m.status === 'loaded' ? `<a href="/" class="btn btn-sm btn-outline-success">💬 Chat</a>` : ''}
            </td>
        </tr>
    `).join('');
}

async function downloadModel(modelId, provider) {
    downloadModelId.value = modelId;
    downloadProvider.value = provider;
    startDownload();
}

async function startDownload() {
    const modelId = downloadModelId.value.trim();
    const provider = downloadProvider.value;
    if (!modelId) return;

    downloadProgress.classList.remove('d-none');
    downloadBar.style.width = '0%';
    downloadBar.textContent = '0%';
    downloadStatus.textContent = 'Starting download...';
    btnDownload.disabled = true;

    try {
        const res = await fetch('/api/models/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ modelId, provider })
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
                            downloadBar.textContent = `${data.percent}%`;
                        }
                        downloadStatus.textContent = data.status || '';

                        if (data.status === 'complete' || data.status === 'success') {
                            downloadBar.style.width = '100%';
                            downloadBar.textContent = '100%';
                            downloadBar.classList.remove('progress-bar-animated');
                            downloadStatus.textContent = '✅ Download complete!';
                            setTimeout(() => loadModels(), 1000);
                        }
                        if (data.status === 'error') {
                            downloadBar.classList.add('bg-danger');
                            downloadStatus.textContent = '❌ Download failed';
                        }
                    } catch { }
                }
            }
        }
    } catch (err) {
        downloadStatus.textContent = `❌ Error: ${err.message}`;
        downloadBar.classList.add('bg-danger');
    }

    btnDownload.disabled = false;
}

// Event listeners
providerFilter.addEventListener('change', loadModels);
btnRefresh.addEventListener('click', loadModels);
btnDownload.addEventListener('click', startDownload);

// Make downloadModel available globally for inline onclick
window.downloadModel = downloadModel;

// Init
loadModels();
