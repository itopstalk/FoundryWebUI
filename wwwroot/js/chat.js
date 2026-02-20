// chat.js - Chat interface logic
const chatMessages = document.getElementById('chat-messages');
const chatInput = document.getElementById('chat-input');
const btnSend = document.getElementById('btn-send');
const btnStop = document.getElementById('btn-stop');
const btnNewChat = document.getElementById('btn-new-chat');
const modelSelect = document.getElementById('model-select');
const modelProvider = document.getElementById('model-provider');
const sendText = document.getElementById('send-text');
const sendSpinner = document.getElementById('send-spinner');

let conversation = [];
let abortController = null;

// Load available models
async function loadModels() {
    try {
        const res = await fetch('/api/models/loaded');
        const models = await res.json();
        modelSelect.innerHTML = '';

        if (models.length === 0) {
            modelSelect.innerHTML = '<option value="">No models loaded — go to Models page</option>';
            btnSend.disabled = true;
            return;
        }

        models.forEach(m => {
            const opt = document.createElement('option');
            opt.value = m.id;
            opt.textContent = `${m.name} (${m.provider})`;
            opt.dataset.provider = m.provider;
            modelSelect.appendChild(opt);
        });

        updateProviderBadge();
        btnSend.disabled = false;
    } catch (err) {
        modelSelect.innerHTML = '<option value="">Error loading models</option>';
    }
}

modelSelect.addEventListener('change', updateProviderBadge);

function updateProviderBadge() {
    const selected = modelSelect.selectedOptions[0];
    if (selected && selected.dataset.provider) {
        modelProvider.textContent = selected.dataset.provider;
        modelProvider.className = `badge ${selected.dataset.provider === 'foundry' ? 'bg-info' : 'bg-warning'}`;
    } else {
        modelProvider.textContent = '';
    }
}

// Render messages
function renderMessages() {
    if (conversation.length === 0) {
        chatMessages.innerHTML = `
            <div class="text-center text-muted mt-5">
                <h4>Welcome to FoundryWebUI</h4>
                <p>Select a model and start chatting</p>
            </div>`;
        return;
    }

    chatMessages.innerHTML = conversation.map((msg, i) => {
        const isUser = msg.role === 'user';
        return `
            <div class="d-flex mb-3 ${isUser ? 'justify-content-end' : 'justify-content-start'}">
                <div class="card ${isUser ? 'bg-primary text-white' : 'bg-body-secondary'}" style="max-width: 80%;">
                    <div class="card-body py-2 px-3">
                        <small class="fw-bold">${isUser ? 'You' : '🤖 Assistant'}</small>
                        <div class="mt-1 message-content">${formatContent(msg.content)}</div>
                    </div>
                </div>
            </div>`;
    }).join('');

    chatMessages.scrollTop = chatMessages.scrollHeight;
}

function formatContent(text) {
    // Basic markdown: code blocks, inline code, bold, newlines
    return text
        .replace(/```(\w*)\n([\s\S]*?)```/g, '<pre><code class="language-$1">$2</code></pre>')
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        .replace(/\n/g, '<br>');
}

// Send message
async function sendMessage() {
    const text = chatInput.value.trim();
    if (!text || !modelSelect.value) return;

    const selectedOption = modelSelect.selectedOptions[0];
    const provider = selectedOption.dataset.provider || 'foundry';

    conversation.push({ role: 'user', content: text });
    conversation.push({ role: 'assistant', content: '' });
    renderMessages();

    chatInput.value = '';
    setLoading(true);

    abortController = new AbortController();

    try {
        const res = await fetch(`/api/chat?provider=${provider}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                model: modelSelect.value,
                messages: conversation.slice(0, -1), // exclude empty assistant message
                stream: true,
                temperature: 0.7
            }),
            signal: abortController.signal
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
                        if (data.error) {
                            conversation[conversation.length - 1].content += `\n\n⚠️ Error: ${data.error}`;
                        } else if (data.content) {
                            conversation[conversation.length - 1].content += data.content;
                        }
                        renderMessages();
                    } catch { }
                }
            }
        }
    } catch (err) {
        if (err.name !== 'AbortError') {
            conversation[conversation.length - 1].content += `\n\n⚠️ Error: ${err.message}`;
            renderMessages();
        }
    }

    setLoading(false);
    abortController = null;
}

function setLoading(loading) {
    btnSend.classList.toggle('d-none', loading);
    btnStop.classList.toggle('d-none', !loading);
    chatInput.disabled = loading;
    sendText.classList.toggle('d-none', loading);
    sendSpinner.classList.toggle('d-none', !loading);
}

// Event listeners
btnSend.addEventListener('click', sendMessage);
btnStop.addEventListener('click', () => {
    if (abortController) abortController.abort();
});
btnNewChat.addEventListener('click', () => {
    conversation = [];
    renderMessages();
});
chatInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
    }
});

// Init
loadModels();
