const API = '';
let selectedSpeed = 1;
let statusPollTimer = null;

// ─── Date Helpers ─────────────────────────────────────────
// Форматирует Date в строку для input[type="datetime-local"] (без секунд)
function toDatetimeLocal(date) {
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth()+1)}-${pad(date.getDate())}` +
           `T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function initDefaultDates() {
    const now = new Date();
    // Начало сегодняшнего дня (00:00 в локальном времени)
    const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 0, 0);

    // Events tab
    const filterFrom = document.getElementById('filterFrom');
    const filterTo   = document.getElementById('filterTo');
    if (!filterFrom.value) filterFrom.value = toDatetimeLocal(todayStart);
    if (!filterTo.value)   filterTo.value   = toDatetimeLocal(now);

    // Replay tab
    const replayStart = document.getElementById('replayStart');
    const replayEnd   = document.getElementById('replayEnd');
    if (!replayStart.value) replayStart.value = toDatetimeLocal(todayStart);
    if (!replayEnd.value)   replayEnd.value   = toDatetimeLocal(now);
}

// ─── Tab Navigation ───────────────────────────────────────
document.querySelectorAll('.nav-item').forEach(btn => {
    btn.addEventListener('click', () => {
        document.querySelectorAll('.nav-item').forEach(b => b.classList.remove('active'));
        document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
        btn.classList.add('active');
        document.getElementById('tab' + capitalize(btn.dataset.tab)).classList.add('active');
        localStorage.setItem('activeTab', btn.dataset.tab);
    });
});

function capitalize(s) { 
    return s.split('-').map(word => word.charAt(0).toUpperCase() + word.slice(1)).join(''); 
}

// ─── Settings ─────────────────────────────────────────────
async function loadSettings() {
    try {
        const res = await fetch(`${API}/api/settings`);
        if (!res.ok) return;
        const d = await res.json();

        document.getElementById('retentionHours').value  = d.retentionHours || 24;
        document.getElementById('isRecordingEnabled').checked = d.isRecordingEnabled || false;
        document.getElementById('trackers').value        = (d.trackersWhiteList || []).join('\n');
        document.getElementById('isRtlsRecordingEnabled').checked = d.isRtlsRecordingEnabled || false;

        // Load topics into array
        activeTopics = d.subscribedTopics || [];
        renderTopicChips();

        updateTrackerChips();
        updateRecordingBadges(d.isRecordingEnabled || false, d.isRtlsRecordingEnabled || false);
    } catch (e) {
        console.warn('Could not load settings:', e);
    }
}

function updateTrackerChips() {
    const vals = document.getElementById('trackers').value.split('\n').map(s => s.trim()).filter(Boolean);
    document.getElementById('trackerChips').innerHTML = vals.map(v => `<span class="chip">${escHtml(v)}</span>`).join('');
}

function updateRecordingBadges(mqttEnabled, rtlsEnabled) {
    const mqttBadge = document.getElementById('mqttRecordingBadge');
    if (mqttEnabled) mqttBadge.classList.add('visible');
    else mqttBadge.classList.remove('visible');

    const rtlsBadge = document.getElementById('rtlsRecordingBadge');
    if (rtlsEnabled) rtlsBadge.classList.add('visible');
    else rtlsBadge.classList.remove('visible');
}

// ─── Topic management (in-memory array) ──────────────────
let activeTopics = [];

function renderTopicChips() {
    const container = document.getElementById('topicChips');
    container.innerHTML = activeTopics.length === 0
        ? '<span style="color:var(--text-3);font-size:12px;">Топики не добавлены</span>'
        : activeTopics.map((t, i) => `
            <span class="chip">
                ${escHtml(t)}
                <span class="chip-remove" data-index="${i}" title="Remove">×</span>
            </span>`).join('');

    // Wire up remove buttons
    container.querySelectorAll('.chip-remove').forEach(btn => {
        btn.addEventListener('click', () => {
            activeTopics.splice(parseInt(btn.dataset.index), 1);
            renderTopicChips();
        });
    });

    // Keep hidden input in sync (used by save payload)
    document.getElementById('topics').value = activeTopics.join('\n');
}

function addTopic(val) {
    const t = val.trim();
    if (!t || activeTopics.includes(t)) return;
    activeTopics.push(t);
    renderTopicChips();
}

// Dropdown preset
document.getElementById('topicPreset').addEventListener('change', e => {
    if (e.target.value) {
        addTopic(e.target.value);
        e.target.value = '';   // reset dropdown
    }
});

// Custom input — Enter key or Add button
document.getElementById('topicCustom').addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); addTopic(e.target.value); e.target.value = ''; }
});
document.getElementById('addTopicBtn').addEventListener('click', () => {
    const input = document.getElementById('topicCustom');
    addTopic(input.value);
    input.value = '';
});

renderTopicChips(); // Initial empty render


document.getElementById('trackers').addEventListener('input', updateTrackerChips);
document.getElementById('isRecordingEnabled').addEventListener('change', e => {
    updateRecordingBadges(e.target.checked, document.getElementById('isRtlsRecordingEnabled').checked);
});
document.getElementById('isRtlsRecordingEnabled').addEventListener('change', e => {
    updateRecordingBadges(document.getElementById('isRecordingEnabled').checked, e.target.checked);
});

document.getElementById('saveSettingsBtn').addEventListener('click', async () => {
    const payload = {
        id: 1,
        retentionHours:              parseInt(document.getElementById('retentionHours').value) || 24,
        isRecordingEnabled:          document.getElementById('isRecordingEnabled').checked,
        trackerIdTopicSegmentIndex:  1,
        subscribedTopics:            activeTopics,
        trackersWhiteList:           document.getElementById('trackers').value.split('\n').map(s=>s.trim()).filter(Boolean),
        isRtlsRecordingEnabled:      document.getElementById('isRtlsRecordingEnabled').checked,
    };

    try {
        const res = await fetch(`${API}/api/settings`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (res.ok) showToast('settingsToast');
        updateRecordingBadges(payload.isRecordingEnabled, payload.isRtlsRecordingEnabled);
    } catch (e) { alert('Не удалось сохранить настройки: ' + e.message); }
});

function showToast(id) {
    const t = document.getElementById(id);
    t.classList.add('show');
    setTimeout(() => t.classList.remove('show'), 2000);
}

// ─── Events Tab ───────────────────────────────────────────
document.getElementById('filterEventsBtn').addEventListener('click', loadEvents);
document.getElementById('clearEventsBtn').addEventListener('click', async () => {
    if (!confirm('Удалить ВСЕ записанные события из базы данных?')) return;
    await fetch(`${API}/api/events`, { method: 'DELETE' });
    await loadEvents();
});

async function loadEvents() {
    const from    = document.getElementById('filterFrom').value;
    const to      = document.getElementById('filterTo').value;
    const tracker = document.getElementById('filterTracker').value.trim();

    // Если «по» не указано — используем текущее UTC-время
    const toDate = to ? new Date(to) : new Date();

    let url = `${API}/api/events?pageSize=200`;
    if (from) url += `&from=${encodeURIComponent(new Date(from).toISOString())}`;
    url += `&to=${encodeURIComponent(toDate.toISOString())}`;
    if (tracker) url += `&trackerId=${encodeURIComponent(tracker)}`;

    try {
        const res  = await fetch(url);
        const data = await res.json();
        renderEvents(data.items, data.total);
    } catch (e) {
        document.getElementById('eventsBody').innerHTML =
            `<tr><td colspan="4" class="empty-row">Ошибка загрузки событий</td></tr>`;
    }
}

function renderEvents(items, total) {
    const body = document.getElementById('eventsBody');
    const stats = document.getElementById('eventStats');

    stats.innerHTML = `<div class="stat-chip">Всего <strong>${total}</strong></div>
                       <div class="stat-chip">Показано <strong>${items.length}</strong></div>`;

    if (items.length === 0) {
        body.innerHTML = `<tr><td colspan="4" class="empty-row">События не найдены</td></tr>`;
        return;
    }

    body.innerHTML = items.map(e => `
        <tr>
            <td>${fmt(e.receivedAt)}</td>
            <td><span class="topic-badge">${escHtml(e.endpoint)}</span></td>
            <td>${escHtml(e.trackerId || '—')}</td>
            <td class="payload-preview" data-payload="${escHtml(e.payload)}">${escHtml(preview(e.payload))}</td>
        </tr>
    `).join('');
}

function preview(s) {
    if (!s) return '—';
    return s.length > 80 ? s.slice(0, 80) + '…' : s;
}

function fmt(iso) {
    return new Date(iso).toLocaleString(undefined, { hour12: false });
}

function escHtml(s) {
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

// ─── Modal Logic ──────────────────────────────────────────
function showJsonModal(payloadStr) {
    const modal = document.getElementById('jsonModal');
    const content = document.getElementById('jsonModalContent');
    try {
        const obj = JSON.parse(payloadStr);
        content.textContent = JSON.stringify(obj, null, 2);
    } catch {
        content.textContent = payloadStr; // fallback for raw text
    }
    modal.classList.add('show');
}

document.getElementById('closeJsonModal')?.addEventListener('click', () => {
    document.getElementById('jsonModal').classList.remove('show');
});

document.getElementById('jsonModal')?.addEventListener('click', (e) => {
    if (e.target === document.getElementById('jsonModal')) {
        document.getElementById('jsonModal').classList.remove('show');
    }
});

document.getElementById('eventsBody').addEventListener('click', (e) => {
    const target = e.target.closest('.payload-preview');
    if (target && target.dataset.payload) {
        showJsonModal(target.dataset.payload);
    }
});

// ─── Speed Buttons (shared) ───────────────────────────────
document.querySelectorAll('#speedButtons .speed-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        document.querySelectorAll('#speedButtons .speed-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        selectedSpeed = parseFloat(btn.dataset.speed);
        document.getElementById('replaySpeed').value = selectedSpeed;
    });
});

// ─── Unified Replay ───────────────────────────────────────
const playBtn = document.getElementById('playBtn');
const stopBtn = document.getElementById('stopBtn');

document.getElementById('replayForm').addEventListener('submit', async e => {
    e.preventDefault();

    const startTime = new Date(document.getElementById('replayStart').value).toISOString();
    const endTime   = new Date(document.getElementById('replayEnd').value).toISOString();

    const filterVal = document.getElementById('commonReplayTrackerFilter').value;
    const targetTracker = document.getElementById('commonReplayTargetTracker').value.trim() || null;

    const mqttCmd = {
        startTime,
        endTime,
        speedMultiplier:  selectedSpeed,
        trackerFilter:    filterVal ? [filterVal] : null,
        targetTrackerId:  targetTracker
    };

    const rtlsCmd = {
        startTime,
        endTime,
        speedMultiplier: selectedSpeed,
        macFilter:       filterVal ? [filterVal] : null,
        targetNativeId:  targetTracker
    };

    const [mqttRes, rtlsRes] = await Promise.allSettled([
        fetch(`${API}/api/replay/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(mqttCmd)
        }),
        fetch(`${API}/api/rtls-replay/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(rtlsCmd)
        })
    ]);

    const mqttOk = mqttRes.status === 'fulfilled' && mqttRes.value.ok;
    const rtlsOk = rtlsRes.status === 'fulfilled' && rtlsRes.value.ok;

    if (mqttOk || rtlsOk) {
        setReplayUI(mqttOk, rtlsOk);
    } else {
        alert('Не удалось запустить воспроизведение');
    }
});

stopBtn.addEventListener('click', async () => {
    await Promise.allSettled([
        fetch(`${API}/api/replay/stop`, { method: 'POST' }),
        fetch(`${API}/api/rtls-replay/stop`, { method: 'POST' })
    ]);
    setReplayUI(false, false);
});

function setReplayUI(mqttPlaying, rtlsPlaying) {
    const anyPlaying = mqttPlaying || rtlsPlaying;
    playBtn.disabled = anyPlaying;
    stopBtn.disabled = !anyPlaying;

    // Disable form inputs during replay
    const inputsToDisable = ['replayStart', 'replayEnd', 'commonReplayTargetTracker', 'commonReplayTrackerFilter'];
    inputsToDisable.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.disabled = anyPlaying;
    });
    document.querySelectorAll('#speedButtons .speed-btn').forEach(btn => {
        btn.disabled = anyPlaying;
    });

    // MQTT status
    const mqttDot   = document.querySelector('#statusIndicator .status-dot');
    const mqttLabel = document.getElementById('statusLabel');
    const mqttProg  = document.getElementById('progressInfo');
    if (mqttPlaying) {
        mqttDot.className = 'status-dot playing';
        mqttLabel.textContent = 'MQTT — Воспроизведение…';
        mqttProg.style.display = 'flex';
    } else {
        mqttDot.className = 'status-dot idle';
        mqttLabel.textContent = 'MQTT — Ожидание';
        mqttProg.style.display = 'none';
        document.getElementById('progressBar').style.width = '0%';
        document.getElementById('progressText').textContent = 'MQTT: 0 / 0';
        const ce = document.getElementById('currentEvent');
        if (ce) ce.style.display = 'none';
    }

    // RTLS status
    const rtlsDot   = document.querySelector('#rtlsStatusIndicator .status-dot');
    const rtlsLabel = document.getElementById('rtlsStatusLabel');
    const rtlsProg  = document.getElementById('rtlsProgressInfo');
    if (rtlsPlaying) {
        rtlsDot.className = 'status-dot playing';
        rtlsLabel.textContent = 'RTLS — Воспроизведение…';
        rtlsProg.style.display = 'flex';
    } else {
        rtlsDot.className = 'status-dot idle';
        rtlsLabel.textContent = 'RTLS — Ожидание';
        rtlsProg.style.display = 'none';
        document.getElementById('rtlsProgressBar').style.width = '0%';
        document.getElementById('rtlsProgressText').textContent = 'RTLS: 0 / 0';
    }
}

async function pollStatus() {
    try {
        const res = await fetch(`${API}/api/replay/status`);
        if (!res.ok) return;
        const data = await res.json();
        const p = data.progress;
        const playing = data.isPlaying;

        if (playing && p && p.total > 0) {
            const pct = Math.round((p.sent / p.total) * 100);
            document.getElementById('progressBar').style.width = pct + '%';
            document.getElementById('progressText').textContent = `MQTT: ${p.sent} / ${p.total} (${pct}%)`;
            if (p.currentTopic) {
                const el = document.getElementById('currentEvent');
                el.style.display = 'block';
                el.textContent = `▶ ${p.currentTopic}  |  ${p.currentEventTime ? fmt(p.currentEventTime) : ''}`;
            }
        }

        if (!playing && playBtn.disabled) setReplayUI(false, !document.querySelector('#rtlsStatusIndicator .status-dot').classList.contains('idle'));
    } catch (e) {}
}

async function pollRtlsStatus() {
    try {
        const res = await fetch(`${API}/api/rtls-replay/status`);
        if (!res.ok) return;
        const data = await res.json();
        const playing = data.isPlaying;

        if (playing && data.totalSessionEvents > 0) {
            const pct = Math.round((data.processedCount / data.totalSessionEvents) * 100);
            document.getElementById('rtlsProgressBar').style.width = pct + '%';
            document.getElementById('rtlsProgressText').textContent = `RTLS: ${data.processedCount} / ${data.totalSessionEvents} (${pct}%)`;
        }

        if (!playing && !document.querySelector('#rtlsStatusIndicator .status-dot').classList.contains('idle')) {
            const mqttPlaying = !document.querySelector('#statusIndicator .status-dot').classList.contains('idle');
            setReplayUI(mqttPlaying, false);
        }
    } catch (e) {}
}

async function loadUnifiedTrackers() {
    try {
        const [mqttRes, rtlsRes] = await Promise.all([
            fetch(`${API}/api/events/trackers`),
            fetch(`${API}/api/rtls-events/macs`)
        ]);
        let trackers = [];
        if (mqttRes.ok) trackers.push(...(await mqttRes.json()));
        if (rtlsRes.ok) trackers.push(...(await rtlsRes.json()));
        
        trackers = [...new Set(trackers)]; // deduplicate

        document.getElementById('commonReplayTrackerFilter').innerHTML =
            '<option value="">-- Выбрать NativeId --</option>' +
            trackers.map(t => `<option value="${t}">${escHtml(t)}</option>`).join('');
    } catch (e) {}
}

// ─── Live Events (SignalR) ────────────────────────────────
const mqttLiveBody = document.getElementById('liveMqttBody');
const rtlsLiveBody = document.getElementById('liveRtlsBody');
const MAX_LIVE_ROWS = 100;

function appendLiveRow(tbody, html) {
    if (tbody.querySelector('.empty-row')) {
        tbody.innerHTML = '';
    }
    const tr = document.createElement('tr');
    tr.innerHTML = html;
    tbody.prepend(tr);

    while (tbody.children.length > MAX_LIVE_ROWS) {
        tbody.removeChild(tbody.lastChild);
    }
}

let connection;
if (typeof signalR !== 'undefined') {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/api/live-events")
        .withAutomaticReconnect()
        .build();

    connection.on("ReceiveMqttEvent", (event) => {
        const html = `
            <td>${fmt(event.receivedAt)}</td>
            <td><span class="topic-badge">${escHtml(event.topic)}</span></td>
            <td>${escHtml(event.trackerId || '—')}</td>
        `;
        appendLiveRow(mqttLiveBody, html);
    });

    connection.on("ReceiveRtlsEvent", (event) => {
        const html = `
            <td>${fmt(event.receivedAt)}</td>
            <td><span class="topic-badge">${escHtml(event.macAddress)}</span></td>
        `;
        appendLiveRow(rtlsLiveBody, html);
    });
}

async function startSignalR() {
    if (!connection) return;
    try {
        await connection.start();
        console.log("SignalR Connected.");
    } catch (err) {
        console.error("SignalR Connection Error: ", err);
        setTimeout(startSignalR, 5000);
    }
}

// ─── Init ─────────────────────────────────────────────────
function initAll() {
    loadSettings();
    initDefaultDates();
    loadUnifiedTrackers();
    startSignalR();
    setInterval(pollStatus, 2000);
    setInterval(pollRtlsStatus, 2000);

    // Restore tab after a small delay to ensure all DOM state is stable
    setTimeout(() => {
        let savedTab = localStorage.getItem('activeTab');
        // Migrate legacy key: rtls-replay tab was merged into replay
        if (savedTab === 'rtls-replay') {
            savedTab = 'replay';
            localStorage.setItem('activeTab', 'replay');
        }
        if (savedTab) {
            const btn = document.querySelector(`.nav-item[data-tab="${savedTab}"]`);
            if (btn) {
                btn.click();
            }
        }
    }, 100);
}

initAll();
