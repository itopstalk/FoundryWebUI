// settings.js - System prompt management + cache directory
const promptsList = document.getElementById('prompts-list');
const btnAddPrompt = document.getElementById('btn-add-prompt');
const btnSavePrompt = document.getElementById('btn-save-prompt');
const promptName = document.getElementById('prompt-name');
const promptContent = document.getElementById('prompt-content');
const promptModalTitle = document.getElementById('prompt-modal-title');

// Cache directory elements
const cacheDirLoading = document.getElementById('cache-dir-loading');
const cacheDirContent = document.getElementById('cache-dir-content');
const cacheDirPath = document.getElementById('cache-dir-path');
const btnSaveCacheDir = document.getElementById('btn-save-cache-dir');
const cacheDirStatus = document.getElementById('cache-dir-status');

let editingId = null;
let promptModal = null;
let originalCachePath = '';

// Foundry CLI info elements
const foundryInfoLoading = document.getElementById('foundry-info-loading');
const foundryInfoContent = document.getElementById('foundry-info-content');
const foundryInfoIcon = document.getElementById('foundry-info-icon');
const foundryInfoPath = document.getElementById('foundry-info-path');

document.addEventListener('DOMContentLoaded', () => {
    promptModal = new bootstrap.Modal(document.getElementById('prompt-modal'));
    loadPrompts();
    loadCacheDirectory();
    loadFoundryInfo();
});

// ============================================================
// Foundry CLI Info
// ============================================================

async function loadFoundryInfo() {
    try {
        const res = await fetch('/api/settings/foundry-info');
        const data = await res.json();
        foundryInfoLoading.style.display = 'none';
        foundryInfoContent.style.display = 'block';
        if (data.found) {
            foundryInfoIcon.innerHTML = '<span class="badge bg-success">Found</span>';
            foundryInfoPath.textContent = data.executablePath;
        } else {
            foundryInfoIcon.innerHTML = '<span class="badge bg-danger">Not Found</span>';
            foundryInfoPath.textContent = data.executablePath === 'foundry' ? 'foundry.exe not found on this system' : data.executablePath;
        }
    } catch (err) {
        foundryInfoLoading.innerHTML = `<span class="text-danger small">Could not detect Foundry CLI: ${err.message}</span>`;
    }
}

// ============================================================
// Cache Directory
// ============================================================

async function loadCacheDirectory() {
    try {
        const res = await fetch('/api/settings/cache-directory');
        const data = await res.json();
        cacheDirLoading.style.display = 'none';
        cacheDirContent.style.display = 'block';
        if (data.detected) {
            cacheDirPath.value = data.path;
            originalCachePath = data.path;
        } else {
            cacheDirPath.value = '';
            cacheDirPath.placeholder = 'Could not detect — enter path manually';
        }
    } catch (err) {
        cacheDirLoading.innerHTML = `<span class="text-danger">Error loading cache directory: ${err.message}</span>`;
    }
}

function showCacheStatus(message, type) {
    cacheDirStatus.style.display = 'block';
    cacheDirStatus.className = `mt-2 small text-${type}`;
    cacheDirStatus.textContent = message;
}

btnSaveCacheDir.addEventListener('click', async () => {
    const newPath = cacheDirPath.value.trim();
    if (!newPath) {
        showCacheStatus('Path is required.', 'danger');
        return;
    }
    if (newPath === originalCachePath) {
        showCacheStatus('No change — path is the same.', 'muted');
        return;
    }

    btnSaveCacheDir.disabled = true;
    btnSaveCacheDir.textContent = '⏳ Saving...';
    cacheDirStatus.style.display = 'none';

    try {
        const res = await fetch('/api/settings/cache-directory', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: newPath })
        });
        const data = await res.json();
        if (res.ok) {
            originalCachePath = data.path;
            showCacheStatus(`✅ Cache directory updated. ${data.message || ''}`, 'success');
        } else {
            showCacheStatus(`❌ ${data.error}`, 'danger');
        }
    } catch (err) {
        showCacheStatus(`❌ ${err.message}`, 'danger');
    } finally {
        btnSaveCacheDir.disabled = false;
        btnSaveCacheDir.textContent = '💾 Save';
    }
});

async function loadPrompts() {
    try {
        const res = await fetch('/api/system-prompts');
        const prompts = await res.json();
        renderPrompts(prompts);
    } catch (err) {
        promptsList.innerHTML = `<div class="text-center text-danger py-4">Error loading prompts: ${err.message}</div>`;
    }
}

function renderPrompts(prompts) {
    if (prompts.length === 0) {
        promptsList.innerHTML = '<div class="text-center text-muted py-4">No system prompts configured.</div>';
        return;
    }

    promptsList.innerHTML = prompts.map(p => `
        <div class="list-group-item d-flex align-items-start gap-3">
            <div class="flex-grow-1">
                <div class="d-flex align-items-center gap-2 mb-1">
                    <strong>${escapeHtml(p.name)}</strong>
                    ${p.isDefault ? '<span class="badge bg-success">Default</span>' : ''}
                </div>
                <div class="text-muted small" style="white-space: pre-wrap; max-height: 80px; overflow: hidden;">${escapeHtml(p.content)}</div>
            </div>
            <div class="d-flex flex-column gap-1" style="min-width: 90px;">
                ${!p.isDefault ? `<button class="btn btn-sm btn-outline-success" onclick="setDefault('${p.id}')" title="Set as default">⭐ Default</button>` : ''}
                <button class="btn btn-sm btn-outline-light" onclick="editPrompt('${p.id}')">✏️ Edit</button>
                ${!p.isDefault ? `<button class="btn btn-sm btn-outline-danger" onclick="deletePrompt('${p.id}', '${escapeHtml(p.name)}')">🗑️ Delete</button>` : ''}
            </div>
        </div>
    `).join('');
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Add new prompt
btnAddPrompt.addEventListener('click', () => {
    editingId = null;
    promptModalTitle.textContent = 'New System Prompt';
    promptName.value = '';
    promptContent.value = '';
    promptModal.show();
});

// Edit existing prompt
async function editPrompt(id) {
    try {
        const res = await fetch(`/api/system-prompts/${id}`);
        if (!res.ok) return;
        const prompt = await res.json();
        editingId = id;
        promptModalTitle.textContent = 'Edit System Prompt';
        promptName.value = prompt.name;
        promptContent.value = prompt.content;
        promptModal.show();
    } catch (err) {
        alert(`Error: ${err.message}`);
    }
}

// Save prompt (create or update)
btnSavePrompt.addEventListener('click', async () => {
    const name = promptName.value.trim();
    const content = promptContent.value.trim();
    if (!name || !content) {
        alert('Name and content are required.');
        return;
    }

    try {
        const url = editingId ? `/api/system-prompts/${editingId}` : '/api/system-prompts';
        const method = editingId ? 'PUT' : 'POST';
        const res = await fetch(url, {
            method,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, content })
        });
        if (res.ok) {
            promptModal.hide();
            await loadPrompts();
        } else {
            const err = await res.json();
            alert(err.error || 'Failed to save prompt.');
        }
    } catch (err) {
        alert(`Error: ${err.message}`);
    }
});

// Set default
async function setDefault(id) {
    try {
        const res = await fetch(`/api/system-prompts/${id}/default`, { method: 'PUT' });
        if (res.ok) await loadPrompts();
    } catch (err) {
        alert(`Error: ${err.message}`);
    }
}

// Delete prompt
async function deletePrompt(id, name) {
    if (!confirm(`Delete system prompt "${name}"?`)) return;
    try {
        const res = await fetch(`/api/system-prompts/${id}`, { method: 'DELETE' });
        if (res.ok) await loadPrompts();
        else {
            const err = await res.json();
            alert(err.error || 'Failed to delete prompt.');
        }
    } catch (err) {
        alert(`Error: ${err.message}`);
    }
}

// Make functions globally available for inline onclick
window.editPrompt = editPrompt;
window.deletePrompt = deletePrompt;
window.setDefault = setDefault;
