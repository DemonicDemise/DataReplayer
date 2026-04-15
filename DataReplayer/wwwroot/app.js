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
    });
});

function capitalize(s) { return s.charAt(0).toUpperCase() + s.slice(1); }

// ─── Settings ─────────────────────────────────────────────
async function loadSettings() {
    try {
        const res = await fetch(`${API}/api/settings`);
        if (!res.ok) return;
        const d = await res.json();

        document.getElementById('mqttHost').value        = d.mqttBrokerHost || 'localhost';
        document.getElementById('mqttPort').value        = d.mqttBrokerPort || 1883;
        document.getElementById('mqttUser').value        = d.mqttUsername   || '';
        document.getElementById('mqttPass').value        = d.mqttPassword   || '';
        document.getElementById('retentionHours').value  = d.retentionHours || 24;
        document.getElementById('isRecordingEnabled').checked = d.isRecordingEnabled || false;
        document.getElementById('trackers').value        = (d.trackersWhiteList || []).join('\n');

        // Load topics into array
        activeTopics = d.subscribedTopics || [];
        renderTopicChips();

        updateTrackerChips();
        updateRecordingBadge(d.isRecordingEnabled || false);
    } catch (e) {
        console.warn('Could not load settings:', e);
    }
}

function updateTrackerChips() {
    const vals = document.getElementById('trackers').value.split('\n').map(s => s.trim()).filter(Boolean);
    document.getElementById('trackerChips').innerHTML = vals.map(v => `<span class="chip">${escHtml(v)}</span>`).join('');
}

function updateRecordingBadge(enabled) {
    const badge = document.getElementById('recordingBadge');
    if (enabled) badge.classList.add('visible');
    else badge.classList.remove('visible');
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
document.getElementById('isRecordingEnabled').addEventListener('change', e => updateRecordingBadge(e.target.checked));

document.getElementById('saveSettingsBtn').addEventListener('click', async () => {
    const payload = {
        id: 1,
        mqttBrokerHost:              document.getElementById('mqttHost').value.trim(),
        mqttBrokerPort:              parseInt(document.getElementById('mqttPort').value) || 1883,
        mqttUsername:                document.getElementById('mqttUser').value.trim() || null,
        mqttPassword:                document.getElementById('mqttPass').value || null,
        retentionHours:              parseInt(document.getElementById('retentionHours').value) || 24,
        isRecordingEnabled:          document.getElementById('isRecordingEnabled').checked,
        trackerIdTopicSegmentIndex:  1,
        subscribedTopics:            activeTopics,
        trackersWhiteList:           document.getElementById('trackers').value.split('\n').map(s=>s.trim()).filter(Boolean),
    };

    try {
        const res = await fetch(`${API}/api/settings`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (res.ok) showToast('settingsToast');
        updateRecordingBadge(payload.isRecordingEnabled);
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
            <td class="payload-preview">${escHtml(preview(e.payload))}</td>
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

// ─── Speed Buttons ────────────────────────────────────────
document.querySelectorAll('.speed-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        document.querySelectorAll('.speed-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        selectedSpeed = parseFloat(btn.dataset.speed);
        document.getElementById('replaySpeed').value = selectedSpeed;
    });
});

// ─── Replay ───────────────────────────────────────────────
const playBtn = document.getElementById('playBtn');
const stopBtn = document.getElementById('stopBtn');

document.getElementById('replayForm').addEventListener('submit', async e => {
    e.preventDefault();

    const trackerRaw = document.getElementById('replayTrackerFilter').value.trim();
    const trackerFilter = trackerRaw
        ? trackerRaw.split(',').map(s => s.trim()).filter(Boolean)
        : null;

    const cmd = {
        startTime:         new Date(document.getElementById('replayStart').value).toISOString(),
        endTime:           new Date(document.getElementById('replayEnd').value).toISOString(),
        speedMultiplier:   selectedSpeed,
        trackerFilter:     trackerFilter
    };

    try {
        const res = await fetch(`${API}/api/replay/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(cmd)
        });
        if (res.ok) setReplayUI(true);
        else alert('Не удалось запустить воспроизведение');
    } catch (e) { alert('Ошибка: ' + e.message); }
});

stopBtn.addEventListener('click', async () => {
    try {
        await fetch(`${API}/api/replay/stop`, { method: 'POST' });
        setReplayUI(false);
    } catch (e) {}
});

function setReplayUI(playing) {
    playBtn.disabled = playing;
    stopBtn.disabled = !playing;

    const dot = document.querySelector('.status-dot');
    const label = document.getElementById('statusLabel');
    const progressInfo = document.getElementById('progressInfo');

    if (playing) {
        dot.className = 'status-dot playing';
        label.textContent = 'Воспроизведение…';
        progressInfo.style.display = 'flex';
    } else {
        dot.className = 'status-dot idle';
        label.textContent = 'Ожидание';
        progressInfo.style.display = 'none';
        document.getElementById('currentEvent').style.display = 'none';
        document.getElementById('progressBar').style.width = '0%';
        document.getElementById('progressText').textContent = '—';
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
            document.getElementById('progressText').textContent = `${p.sent} / ${p.total} событий (${pct}%)`;

            if (p.currentTopic) {
                const el = document.getElementById('currentEvent');
                el.style.display = 'block';
                el.textContent = `▶ ${p.currentTopic}  |  ${p.currentEventTime ? fmt(p.currentEventTime) : ''}`;
            }
        }

        if (!playing && !playBtn.disabled) {
            // Session ended naturally
        } else if (!playing && playBtn.disabled) {
            setReplayUI(false);
        }
    } catch (e) {}
}

// ─── Init ─────────────────────────────────────────────────
loadSettings();
initDefaultDates();
setInterval(pollStatus, 2000);
