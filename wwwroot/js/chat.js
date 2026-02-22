// chat.js - Chat interface logic
const chatMessages = document.getElementById('chat-messages');
const chatInput = document.getElementById('chat-input');
const btnSend = document.getElementById('btn-send');
const btnStop = document.getElementById('btn-stop');
const btnNewChat = document.getElementById('btn-new-chat');
const modelSelect = document.getElementById('model-select');
const promptSelect = document.getElementById('prompt-select');
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
            opt.textContent = m.name;
            opt.dataset.provider = m.provider;
            modelSelect.appendChild(opt);
        });

        btnSend.disabled = false;
    } catch (err) {
        modelSelect.innerHTML = '<option value="">Error loading models</option>';
    }
}

// Load system prompts into selector
async function loadSystemPrompts() {
    try {
        const res = await fetch('/api/system-prompts');
        const prompts = await res.json();
        promptSelect.innerHTML = '<option value="">None</option>';
        prompts.forEach(p => {
            const opt = document.createElement('option');
            opt.value = p.id;
            opt.textContent = p.name;
            opt.dataset.content = p.content;
            if (p.isDefault) opt.selected = true;
            promptSelect.appendChild(opt);
        });
    } catch (err) {
        console.warn('Failed to load system prompts:', err);
    }
}

function getSystemPromptContent() {
    const opt = promptSelect.selectedOptions[0];
    return opt && opt.dataset.content ? opt.dataset.content : null;
}

modelSelect.addEventListener('change', () => {});

// Render messages
function renderMessages() {
    if (conversation.length === 0) {
        chatMessages.innerHTML = `
            <div class="text-center text-muted mt-5">
                <h4>Welcome to FoundryLocalWebUI</h4>
                <p>Select a model and start chatting</p>
            </div>`;
        return;
    }

    chatMessages.innerHTML = conversation.map((msg, i) => {
        const isUser = msg.role === 'user';
        const contextWarning = msg.contextExceeded
            ? `<div class="alert alert-warning py-1 px-2 mt-2 mb-0 small d-flex align-items-center gap-2">
                 <span style="font-size:1.2em;">🚫</span>
                 <span>Context limit reached — this model's token window is full. <strong>Start a new chat</strong> to continue.</span>
               </div>`
            : '';
        return `
            <div class="d-flex mb-3 ${isUser ? 'justify-content-end' : 'justify-content-start'}">
                <div class="card ${isUser ? 'bg-primary text-white' : 'bg-body-secondary'}" style="max-width: 80%;">
                    <div class="card-body py-2 px-3">
                        <small class="fw-bold">${isUser ? 'You' : '🤖 Assistant'}</small>
                        <div class="mt-1 message-content">${formatContent(msg.content)}</div>
                        ${contextWarning}
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
    conversation.push({ role: 'assistant', content: '⏳ Thinking...' });
    const thinkingIdx = conversation.length - 1;
    renderMessages();

    chatInput.value = '';
    setLoading(true);

    abortController = new AbortController();
    let receivedContent = false;

    try {
        console.log(`[chat] Sending to /api/chat?provider=${provider}, model=${modelSelect.value}`);
        // Build messages array with optional system prompt
        const chatMessages_arr = conversation.filter((m, i) => i < thinkingIdx).map(m => ({role: m.role, content: m.content}));
        const sysPrompt = getSystemPromptContent();
        if (sysPrompt) {
            chatMessages_arr.unshift({ role: 'system', content: sysPrompt });
        }
        const res = await fetch(`/api/chat?provider=${provider}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                model: modelSelect.value,
                messages: chatMessages_arr,
                stream: true,
                temperature: 0.7
            }),
            signal: abortController.signal
        });

        console.log(`[chat] Response status: ${res.status} ${res.statusText}`);

        if (!res.ok) {
            let errText = '';
            try { errText = await res.text(); } catch {}
            conversation[thinkingIdx].content = `⚠️ HTTP ${res.status}: ${errText || res.statusText}`;
            renderMessages();
            setLoading(false);
            abortController = null;
            return;
        }

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                console.log('[chat] Stream ended');
                break;
            }

            const chunk = decoder.decode(value, { stream: true });
            console.log('[chat] Raw chunk:', chunk);
            buffer += chunk;
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';

            for (const line of lines) {
                if (!line.trim()) continue;
                console.log('[chat] Line:', line);

                if (line.startsWith('data: ')) {
                    const dataStr = line.substring(6);
                    try {
                        const data = JSON.parse(dataStr);
                        if (!receivedContent && (data.content || data.error)) {
                            conversation[thinkingIdx].content = '';
                            receivedContent = true;
                        }
                        if (data.content) {
                            conversation[thinkingIdx].content += data.content;
                        }
                        if (data.error) {
                            if (data.error === 'context_length_exceeded') {
                                conversation[thinkingIdx].content += '\n\n⚠️ **Context limit reached** — The conversation is too long for this model. Start a new chat or use a model with a larger context window.';
                                conversation[thinkingIdx].contextExceeded = true;
                            } else {
                                conversation[thinkingIdx].content += `\n\n⚠️ Error: ${data.error}`;
                            }
                        }
                        renderMessages();
                    } catch (parseErr) {
                        console.warn('[chat] Failed to parse:', dataStr, parseErr);
                    }
                } else if (line.startsWith('event: ')) {
                    // SSE event type line — skip, data is on next line
                    console.log('[chat] Event type:', line.substring(7));
                }
            }
        }

        if (!receivedContent) {
            console.warn('[chat] No content received from stream');
            conversation[thinkingIdx].content = '⚠️ No response received. The model may still be loading — try again in a moment.';
            renderMessages();
        }
    } catch (err) {
        console.error('[chat] Error:', err);
        if (err.name !== 'AbortError') {
            conversation[thinkingIdx].content = `⚠️ Error: ${err.message}`;
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
loadSystemPrompts();
