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
const showThinkingToggle = document.getElementById('show-thinking');
const maxTokensSlider = document.getElementById('max-tokens-slider');
const maxTokensValue = document.getElementById('max-tokens-value');

let conversation = [];
let abortController = null;
let modelMaxTokens = {}; // modelId -> maxOutputTokens

// Max tokens slider display
if (maxTokensSlider) {
    maxTokensSlider.addEventListener('input', () => {
        maxTokensValue.textContent = maxTokensSlider.value;
    });
}

// Thinking token patterns: content before the answer marker is "thinking"
const THINKING_MARKERS = [
    { start: '<|channel|>analysis', end: '<|message|>' },
    { start: '<think>', end: '</think>' }
];

function parseThinkingAndAnswer(text) {
    for (const marker of THINKING_MARKERS) {
        const startIdx = text.indexOf(marker.start);
        if (startIdx === -1) continue;
        const afterStart = startIdx + marker.start.length;
        const endIdx = text.indexOf(marker.end, afterStart);
        if (endIdx !== -1) {
            const thinking = text.substring(afterStart, endIdx).trim();
            const answer = text.substring(endIdx + marker.end.length).trim();
            return { thinking, answer, hasThinking: true };
        } else {
            // Thinking started but answer not yet received — all content after marker is thinking-in-progress
            const thinking = text.substring(afterStart).trim();
            return { thinking, answer: '', hasThinking: true, thinkingInProgress: true };
        }
    }
    return { thinking: '', answer: text, hasThinking: false };
}

// Load available models
async function loadModels() {
    try {
        // Fetch loaded models and full catalog (for maxOutputTokens) in parallel
        const [loadedRes, catalogRes] = await Promise.all([
            fetch('/api/models/loaded'),
            fetch('/api/models?provider=foundry')
        ]);
        const models = await loadedRes.json();

        // Build maxTokens lookup from catalog
        if (catalogRes.ok) {
            const catalog = await catalogRes.json();
            catalog.forEach(m => {
                if (m.maxOutputTokens) modelMaxTokens[m.id] = m.maxOutputTokens;
            });
        }

        modelSelect.innerHTML = '';

        if (models.length === 0) {
            modelSelect.innerHTML = '<option value="">No models loaded -- go to Models page</option>';
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
        updateMaxTokensSlider();
    } catch (err) {
        modelSelect.innerHTML = '<option value="">Error loading models</option>';
    }
}

function updateMaxTokensSlider() {
    if (!maxTokensSlider) return;
    const selectedModel = modelSelect.value;
    const limit = modelMaxTokens[selectedModel] || 2048;
    maxTokensSlider.max = limit;
    if (parseInt(maxTokensSlider.value) > limit) {
        maxTokensSlider.value = limit;
    }
    maxTokensValue.textContent = maxTokensSlider.value;
}

modelSelect.addEventListener('change', updateMaxTokensSlider);

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

    const showThinking = showThinkingToggle && showThinkingToggle.checked;

    chatMessages.innerHTML = conversation.map((msg, i) => {
        const isUser = msg.role === 'user';
        const contextWarning = msg.contextExceeded
            ? `<div class="alert alert-warning py-1 px-2 mt-2 mb-0 small d-flex align-items-center gap-2">
                 <span style="font-size:1.2em;">🚫</span>
                 <span>Context limit reached -- this model's token window is full. <strong>Start a new chat</strong> to continue.</span>
               </div>`
            : '';

        if (isUser) {
            return `
                <div class="d-flex mb-3 justify-content-end">
                    <div class="card bg-primary text-white" style="max-width: 80%;">
                        <div class="card-body py-2 px-3">
                            <small class="fw-bold">You</small>
                            <div class="mt-1 message-content">${formatContent(msg.content)}</div>
                        </div>
                    </div>
                </div>`;
        }

        // Assistant message — parse thinking vs answer
        const parsed = parseThinkingAndAnswer(msg.content);
        let html = '';

        if (parsed.hasThinking && showThinking && parsed.thinking) {
            html += `
                <div class="d-flex mb-2 justify-content-start">
                    <div class="card border-secondary" style="max-width: 80%; opacity: 0.75;">
                        <div class="card-body py-2 px-3">
                            <small class="fw-bold text-warning">🧠 Assistant - Thinking</small>
                            <div class="mt-1 message-content thinking-content">${formatContent(parsed.thinking)}</div>
                            ${parsed.thinkingInProgress ? '<div class="text-warning small mt-1"><em>⏳ Still thinking...</em></div>' : ''}
                        </div>
                    </div>
                </div>`;
        }

        if (parsed.answer || !parsed.hasThinking) {
            const displayContent = parsed.hasThinking ? parsed.answer : msg.content;
            const label = parsed.hasThinking ? '🤖 Assistant - Answer' : '🤖 Assistant';
            html += `
                <div class="d-flex mb-3 justify-content-start">
                    <div class="card bg-body-secondary" style="max-width: 80%;">
                        <div class="card-body py-2 px-3">
                            <small class="fw-bold">${label}</small>
                            <div class="mt-1 message-content">${formatContent(displayContent)}</div>
                            ${contextWarning}
                        </div>
                    </div>
                </div>`;
        } else if (parsed.hasThinking && !parsed.answer && !showThinking) {
            // Thinking in progress but toggle is off — show a waiting indicator
            html += `
                <div class="d-flex mb-3 justify-content-start">
                    <div class="card bg-body-secondary" style="max-width: 80%;">
                        <div class="card-body py-2 px-3">
                            <small class="fw-bold">🤖 Assistant</small>
                            <div class="mt-1 message-content"><em>⏳ Thinking...</em></div>
                        </div>
                    </div>
                </div>`;
        }

        return html;
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
        const maxTokens = maxTokensSlider ? parseInt(maxTokensSlider.value) : 4096;
        const res = await fetch(`/api/chat?provider=${provider}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                model: modelSelect.value,
                messages: chatMessages_arr,
                stream: true,
                temperature: 0.7,
                max_tokens: maxTokens
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
            buffer += chunk;
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';

            for (const line of lines) {
                if (!line.trim()) continue;

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
                                conversation[thinkingIdx].content += '\n\n⚠️ **Context limit reached** -- The conversation is too long for this model. Start a new chat or use a model with a larger context window.';
                                conversation[thinkingIdx].contextExceeded = true;
                            } else if (data.error === 'connection_closed') {
                                conversation[thinkingIdx].content += '\n\n⚠️ **Connection lost** -- Foundry Local closed the connection. This usually means the max tokens setting exceeds the model\'s capacity. Try lowering Max Tokens.';
                            } else {
                                conversation[thinkingIdx].content += `\n\n⚠️ Error: ${data.error}`;
                            }
                        }
                        renderMessages();
                    } catch (parseErr) {
                        console.warn('[chat] Failed to parse:', dataStr, parseErr);
                    }
                } else if (line.startsWith('event: ')) {
                    console.log('[chat] Event type:', line.substring(7));
                }
            }
        }

        if (!receivedContent) {
            console.warn('[chat] No content received from stream');
            conversation[thinkingIdx].content = '⚠️ No response received. The model may still be loading -- try again in a moment.';
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
if (showThinkingToggle) {
    showThinkingToggle.addEventListener('change', renderMessages);
}

// Init
loadModels();
loadSystemPrompts();
