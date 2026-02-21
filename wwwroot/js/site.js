// site.js - Global: check Foundry Local status on load and manage reconnect

async function checkProviderStatus() {
    try {
        const res = await fetch('/api/status');
        const statuses = await res.json();
        const foundry = statuses.find(s => s.provider === 'foundry') || statuses[0];

        // Update navbar indicator (bright green/red square)
        const navLight = document.getElementById('foundry-nav-light');
        const navStatus = document.getElementById('foundry-nav-status');
        if (navLight && foundry) {
            navLight.style.background = foundry.isAvailable ? '#00ff00' : '#ff0000';
            navStatus.title = foundry.isAvailable
                ? `Connected — ${foundry.endpoint || ''}`
                : `Disconnected${foundry.error ? ' — ' + foundry.error : ''}`;
        }

        // Update status indicator on the chat page
        if (foundry) {
            const indicator = document.getElementById('foundry-status-indicator');
            const endpointDisplay = document.getElementById('foundry-endpoint-display');

            if (indicator) {
                indicator.textContent = foundry.isAvailable ? 'Connected ✓' : 'Disconnected ✗';
                indicator.className = `badge ${foundry.isAvailable ? 'bg-success' : 'bg-danger'}`;
            }
            if (endpointDisplay) {
                if (foundry.isAvailable && foundry.endpoint) {
                    try {
                        const url = new URL(foundry.endpoint);
                        endpointDisplay.textContent = `Port ${url.port || '80'} — ${foundry.endpoint}`;
                    } catch {
                        endpointDisplay.textContent = foundry.endpoint;
                    }
                } else if (foundry.error) {
                    endpointDisplay.textContent = foundry.error;
                } else {
                    endpointDisplay.textContent = '';
                }
            }
        }

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

                // Refresh navbar badge and model list
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
