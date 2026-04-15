const API = '';
let selectedSpeed = 1;
let statusPollTimer = null;

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
        document.getElementById('topics').value  = (d.subscribedTopics   || []).join('\n');
        document.getElementById('trackers').value = (d.trackersWhiteList || []).join('\n');

        updateChips('topics',   'topicChips');
        updateChips('trackers', 'trackerChips');
        updateRecordingBadge(d.isRecordingEnabled);
    } catch (e) {
        console.warn('Could not load settings:', e);
    }
}

function updateChips(textareaId, chipsId) {
    const textarea = document.getElementById(textareaId);
    const chips = document.getElementById(chipsId);
    const vals = textarea.value.split('\n').map(s => s.trim()).filter(Boolean);
    chips.innerHTML = vals.map(v => `<span class="chip">${escHtml(v)}</span>`).join('');
}

function updateRecordingBadge(enabled) {
    const badge = document.getElementById('recordingBadge');
    if (enabled) badge.classList.add('visible');
    else badge.classList.remove('visible');
}

document.getElementById('topics').addEventListener('input',   () => updateChips('topics',   'topicChips'));
document.getElementById('trackers').addEventListener('input', () => updateChips('trackers', 'trackerChips'));
document.getElementById('isRecordingEnabled').addEventListener('change', e => updateRecordingBadge(e.target.checked));

document.getElementById('saveSettingsBtn').addEventListener('click', async () => {
    const payload = {
        id: 1,
        mqttBrokerHost:       document.getElementById('mqttHost').value.trim(),
        mqttBrokerPort:       parseInt(document.getElementById('mqttPort').value) || 1883,
        mqttUsername:         document.getElementById('mqttUser').value.trim() || null,
        mqttPassword:         document.getElementById('mqttPass').value || null,
        retentionHours:       parseInt(document.getElementById('retentionHours').value) || 24,
        isRecordingEnabled:   document.getElementById('isRecordingEnabled').checked,
        subscribedTopics:     document.getElementById('topics').value.split('\n').map(s=>s.trim()).filter(Boolean),
        trackersWhiteList:    document.getElementById('trackers').value.split('\n').map(s=>s.trim()).filter(Boolean),
    };

    try {
        const res = await fetch(`${API}/api/settings`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (res.ok) showToast('settingsToast');
        updateRecordingBadge(payload.isRecordingEnabled);
    } catch (e) { alert('Failed to save settings: ' + e.message); }
});

function showToast(id) {
    const t = document.getElementById(id);
    t.classList.add('show');
    setTimeout(() => t.classList.remove('show'), 2000);
}

// ─── Events Tab ───────────────────────────────────────────
document.getElementById('filterEventsBtn').addEventListener('click', loadEvents);
document.getElementById('clearEventsBtn').addEventListener('click', async () => {
    if (!confirm('Delete ALL recorded events from the database?')) return;
    await fetch(`${API}/api/events`, { method: 'DELETE' });
    await loadEvents();
});

async function loadEvents() {
    const from    = document.getElementById('filterFrom').value;
    const to      = document.getElementById('filterTo').value;
    const tracker = document.getElementById('filterTracker').value.trim();

    let url = `${API}/api/events?pageSize=200`;
    if (from) url += `&from=${encodeURIComponent(new Date(from).toISOString())}`;
    if (to)   url += `&to=${encodeURIComponent(new Date(to).toISOString())}`;
    if (tracker) url += `&trackerId=${encodeURIComponent(tracker)}`;

    try {
        const res  = await fetch(url);
        const data = await res.json();
        renderEvents(data.items, data.total);
    } catch (e) {
        document.getElementById('eventsBody').innerHTML =
            `<tr><td colspan="4" class="empty-row">Error loading events</td></tr>`;
    }
}

function renderEvents(items, total) {
    const body = document.getElementById('eventsBody');
    const stats = document.getElementById('eventStats');

    stats.innerHTML = `<div class="stat-chip">Total <strong>${total}</strong></div>
                       <div class="stat-chip">Showing <strong>${items.length}</strong></div>`;

    if (items.length === 0) {
        body.innerHTML = `<tr><td colspan="4" class="empty-row">No events found</td></tr>`;
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
        timestampJsonPath: document.getElementById('replayTimePath').value,
        trackerFilter:     trackerFilter
    };

    try {
        const res = await fetch(`${API}/api/replay/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(cmd)
        });
        if (res.ok) setReplayUI(true);
        else alert('Failed to start replay');
    } catch (e) { alert('Error: ' + e.message); }
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
        label.textContent = 'Replaying…';
        progressInfo.style.display = 'flex';
    } else {
        dot.className = 'status-dot idle';
        label.textContent = 'Idle';
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
            document.getElementById('progressText').textContent = `${p.sent} / ${p.total} events (${pct}%)`;

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
setInterval(pollStatus, 2000);
