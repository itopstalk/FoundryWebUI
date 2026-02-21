// site.js - Global: check provider status on load and manage reconnect

async function checkProviderStatus() {
    try {
        const res = await fetch('/api/status');
        const statuses = await res.json();

        // Update navbar badges
        const container = document.getElementById('provider-status');
        if (container) {
            container.innerHTML = statuses.map(s =>
                `<span class="badge ${s.isAvailable ? 'bg-success' : 'bg-danger'}" title="${s.error || s.endpoint || ''}">
                    ${s.provider} ${s.isAvailable ? '✓' : '✗'}
                </span>`
            ).join('');
        }

        // Update per-provider status indicators on the chat page
        statuses.forEach(s => {
            const indicator = document.getElementById(`${s.provider}-status-indicator`);
            const endpointDisplay = document.getElementById(`${s.provider}-endpoint-display`);

            if (indicator) {
                indicator.textContent = s.isAvailable ? 'Connected ✓' : 'Disconnected ✗';
                indicator.className = `badge ${s.isAvailable ? 'bg-success' : 'bg-danger'}`;
            }
            if (endpointDisplay) {
                if (s.isAvailable && s.endpoint) {
                    // Extract port from endpoint URL
                    try {
                        const url = new URL(s.endpoint);
                        endpointDisplay.textContent = `Port ${url.port || '80'} — ${s.endpoint}`;
                    } catch {
                        endpointDisplay.textContent = s.endpoint;
                    }
                } else if (s.error) {
                    endpointDisplay.textContent = s.error;
                } else {
                    endpointDisplay.textContent = '';
                }
            }
        });

        return statuses;
    } catch {
        return [];
    }
}

// Reconnect handler
document.addEventListener('DOMContentLoaded', () => {
    const btnReconnect = document.getElementById('btn-reconnect-foundry');
    if (btnReconnect) {
        btnReconnect.addEventListener('click', async () => {
            const indicator = document.getElementById('foundry-status-indicator');
            const endpointDisplay = document.getElementById('foundry-endpoint-display');

            btnReconnect.disabled = true;
            btnReconnect.textContent = '⏳ Reconnecting...';
            if (indicator) {
                indicator.textContent = 'Reconnecting...';
                indicator.className = 'badge bg-warning';
            }
            if (endpointDisplay) endpointDisplay.textContent = '';

            try {
                const res = await fetch('/api/reconnect?provider=foundry', { method: 'POST' });
                const status = await res.json();

                if (indicator) {
                    indicator.textContent = status.isAvailable ? 'Connected ✓' : 'Disconnected ✗';
                    indicator.className = `badge ${status.isAvailable ? 'bg-success' : 'bg-danger'}`;
                }
                if (endpointDisplay) {
                    if (status.isAvailable && status.endpoint) {
                        try {
                            const url = new URL(status.endpoint);
                            endpointDisplay.textContent = `Port ${url.port || '80'} — ${status.endpoint}`;
                        } catch {
                            endpointDisplay.textContent = status.endpoint;
                        }
                    } else if (status.error) {
                        endpointDisplay.textContent = status.error;
                    }
                }

                // Refresh navbar badges and model list
                await checkProviderStatus();
                if (typeof loadModels === 'function') loadModels();

            } catch (err) {
                if (indicator) {
                    indicator.textContent = 'Error ✗';
                    indicator.className = 'badge bg-danger';
                }
                if (endpointDisplay) endpointDisplay.textContent = err.message;
            }

            btnReconnect.disabled = false;
            btnReconnect.textContent = '🔄 Reconnect';
        });
    }
});

// Initial check on page load
checkProviderStatus();
