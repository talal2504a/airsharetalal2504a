/* ==========================================================================
   AirShare — frontend logic (vanilla JS, no framework)
   ========================================================================== */
(function () {
    'use strict';

    // ---------------------------------------------------------------------
    // Config (backend appsettings.json se match karta hai)
    // ---------------------------------------------------------------------
    const POLL_INTERVAL_MS = 3000;
    const MAX_FILE_BYTES = 25 * 1024 * 1024;
    const MAX_TEXT_CHARS = 20000;
    const NAME_KEY = 'airshare-name';

    const BLOCKED_EXTENSIONS = [
        '.exe', '.bat', '.cmd', '.com', '.scr', '.pif', '.msi', '.msp',
        '.ps1', '.js', '.jse', '.vbs', '.vbe', '.wsf', '.wsh', '.hta',
        '.jar', '.lnk', '.reg', '.cpl', '.msc'
    ];

    // ---------------------------------------------------------------------
    // DOM references
    // ---------------------------------------------------------------------
    const $ = (id) => document.getElementById(id);

    const themeToggle = $('themeToggle');
    const deviceCount = $('deviceCount');
    const avatar = $('avatar');
    const nameInput = $('nameInput');
    const networkIpLabel = $('networkIp');
    const tabFile = $('tabFile');
    const tabText = $('tabText');
    const panelFile = $('panelFile');
    const panelText = $('panelText');
    const dropZone = $('dropZone');
    const fileInput = $('fileInput');
    const fileList = $('fileList');
    const dzHint = $('dzHint');
    const textInput = $('textInput');
    const charCount = $('charCount');
    const expirySelect = $('expirySelect');
    const sendBtn = $('sendBtn');
    const sendBtnLabel = $('sendBtnLabel');
    const progressWrap = $('progressWrap');
    const progressBar = $('progressBar');
    const progressFill = $('progressFill');
    const progressLabel = $('progressLabel');
    const resultBox = $('resultBox');
    const resultText = $('resultText');
    const codeValue = $('codeValue');
    const copyCodeBtn = $('copyCodeBtn');
    const shareList = $('shareList');
    const listCount = $('listCount');
    const emptyState = $('emptyState');
    const toastWrap = $('toastWrap');

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------
    let selectedFiles = [];
    let activeTab = 'file';
    let uploadInFlight = false;
    let pollTimer = null;
    let tickTimer = null;
    let clockOffsetMs = 0;
    const rendered = new Map();

    // =====================================================================
    // FORM SUBMIT BLOCK — page reload rokta hai
    // =====================================================================
    document.addEventListener('submit', function (e) {
        e.preventDefault();
    });

    // =====================================================================
    // DEVICE NAME DETECTION
    // =====================================================================
    function detectDeviceName() {
        const ua = navigator.userAgent || '';

        let os = 'Device';
        if (/iPhone/i.test(ua)) os = 'iPhone';
        else if (/iPad/i.test(ua)) os = 'iPad';
        else if (/Android/i.test(ua)) os = 'Android';
        else if (/Windows/i.test(ua)) os = 'Windows';
        else if (/Mac OS X|Macintosh/i.test(ua)) os = 'Mac';
        else if (/Linux/i.test(ua)) os = 'Linux';

        const suffix = Math.floor(Math.random() * 0xFFFF)
            .toString(16)
            .toUpperCase()
            .padStart(4, '0');

        return os + '-' + suffix;
    }

    function loadName() {
        let saved = null;
        try { saved = localStorage.getItem(NAME_KEY); } catch (_) { }

        if (saved && saved.trim()) {
            nameInput.value = saved;
        } else {
            nameInput.value = detectDeviceName();
            saveName(nameInput.value);
        }
        updateAvatar();
    }

    function saveName(value) {
        try { localStorage.setItem(NAME_KEY, value); } catch (_) { }
    }

    function currentName() {
        const value = nameInput.value.trim();
        return value || 'Unnamed';
    }

    /** Avatar me naam ke pehle 2 letters dikhate hain */
    function updateAvatar() {
        const name = currentName();
        avatar.textContent = name.slice(0, 2).toUpperCase();
    }

    nameInput.addEventListener('input', function () {
        saveName(nameInput.value);
        updateAvatar();
    });

    // =====================================================================
    // THEME TOGGLE
    // =====================================================================
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem('airshare-theme', theme); } catch (_) { }
    }

    (function initTheme() {
        let saved = null;
        try { saved = localStorage.getItem('airshare-theme'); } catch (_) { }

        if (saved === 'light' || saved === 'dark') {
            applyTheme(saved);
        } else if (window.matchMedia
            && window.matchMedia('(prefers-color-scheme: light)').matches) {
            applyTheme('light');
        } else {
            applyTheme('dark');
        }
    })();

    themeToggle.addEventListener('click', function () {
        const current = document.documentElement.getAttribute('data-theme');
        applyTheme(current === 'dark' ? 'light' : 'dark');
    });

    // =====================================================================
    // TABS
    // =====================================================================
    function switchTab(tab) {
        activeTab = tab;

        const isFile = tab === 'file';
        tabFile.classList.toggle('is-active', isFile);
        tabText.classList.toggle('is-active', !isFile);
        tabFile.setAttribute('aria-selected', String(isFile));
        tabText.setAttribute('aria-selected', String(!isFile));

        panelFile.hidden = !isFile;
        panelText.hidden = isFile;
    }

    tabFile.addEventListener('click', function () { switchTab('file'); });
    tabText.addEventListener('click', function () { switchTab('text'); });

    [tabFile, tabText].forEach(function (tab, index, arr) {
        tab.addEventListener('keydown', function (e) {
            if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
            e.preventDefault();
            const next = arr[(index + (e.key === 'ArrowRight' ? 1 : arr.length - 1)) % arr.length];
            next.focus();
            switchTab(next === tabFile ? 'file' : 'text');
        });
    });

    // =====================================================================
    // FILE SELECTION
    // =====================================================================
    function getExtension(name) {
        const dot = name.lastIndexOf('.');
        return dot > -1 ? name.slice(dot).toLowerCase() : '';
    }

    function validateFiles(fileList) {
        const accepted = [];

        for (let i = 0; i < fileList.length; i++) {
            const file = fileList[i];
            const ext = getExtension(file.name);

            if (BLOCKED_EXTENSIONS.indexOf(ext) !== -1) {
                toast("'" + file.name + "' blocked — unsafe file type.", 'error');
                continue;
            }
            if (file.size > MAX_FILE_BYTES) {
                toast(file.name + ' is ' + formatBytes(file.size) + ' — limit is 25 MB.', 'error');
                continue;
            }
            if (file.size === 0) {
                toast(file.name + ' is empty.', 'error');
                continue;
            }
            accepted.push(file);
        }

        return accepted;
    }

    function addFiles(files) {
        selectedFiles = selectedFiles.concat(validateFiles(files));

        const total = selectedFiles.reduce(function (sum, f) { return sum + f.size; }, 0);
        if (total > MAX_FILE_BYTES) {
            toast('Total size exceeded 25 MB.', 'error');
            selectedFiles = [];
        }

        renderSelectedFiles();
    }

    function renderSelectedFiles() {
        fileList.textContent = '';

        selectedFiles.forEach(function (file, index) {
            const li = el('li');

            const name = el('span', 'fl-name', file.name);
            name.title = file.name;

            const size = el('span', 'fl-size', formatBytes(file.size));

            const remove = el('button', 'fl-remove');
            remove.type = 'button';
            remove.setAttribute('aria-label', 'Remove ' + file.name);
            remove.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" '
                + 'stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>';

            remove.addEventListener('click', function () {
                selectedFiles.splice(index, 1);
                renderSelectedFiles();
            });

            li.appendChild(name);
            li.appendChild(size);
            li.appendChild(remove);
            fileList.appendChild(li);
        });
    }

    dropZone.addEventListener('click', function () { fileInput.click(); });

    dropZone.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            fileInput.click();
        }
    });

    fileInput.addEventListener('change', function () {
        addFiles(fileInput.files);
        fileInput.value = '';
    });

    ['dragenter', 'dragover'].forEach(function (evt) {
        dropZone.addEventListener(evt, function (e) {
            e.preventDefault();
            e.stopPropagation();
            dropZone.classList.add('is-dragover');
        });
    });

    ['dragleave', 'drop'].forEach(function (evt) {
        dropZone.addEventListener(evt, function (e) {
            e.preventDefault();
            e.stopPropagation();
            dropZone.classList.remove('is-dragover');
        });
    });

    dropZone.addEventListener('drop', function (e) {
        if (e.dataTransfer && e.dataTransfer.files) {
            addFiles(e.dataTransfer.files);
        }
    });

    ['dragover', 'drop'].forEach(function (evt) {
        window.addEventListener(evt, function (e) {
            if (!dropZone.contains(e.target)) e.preventDefault();
        });
    });

    dzHint.textContent = 'Max 25 MB per file · '
        + BLOCKED_EXTENSIONS.length + ' unsafe types blocked';

    // =====================================================================
    // TEXT TAB
    // =====================================================================
    textInput.addEventListener('input', function () {
        charCount.textContent = String(textInput.value.length);
    });

    // =====================================================================
    // UPLOAD
    // =====================================================================
    sendBtn.addEventListener('click', function () {
        if (uploadInFlight) return;

        const expiryMinutes = parseInt(expirySelect.value, 10);
        const uploaderName = currentName();

        if (activeTab === 'file') {
            if (selectedFiles.length === 0) {
                toast('Select a file first.', 'error');
                return;
            }
            uploadFilesSequentially(selectedFiles, expiryMinutes, uploaderName);
        } else {
            const text = textInput.value.trim();
            if (!text) {
                toast('Type or paste something first.', 'error');
                return;
            }
            if (text.length > MAX_TEXT_CHARS) {
                toast('Text exceeds ' + MAX_TEXT_CHARS.toLocaleString()
                    + ' characters.', 'error');
                return;
            }
            uploadText(text, expiryMinutes, uploaderName);
        }
    });

    function setBusy(isBusy) {
        uploadInFlight = isBusy;
        sendBtn.disabled = isBusy;
        sendBtnLabel.textContent = isBusy ? 'Sending…' : 'Send';
        progressWrap.hidden = !isBusy;

        if (!isBusy) {
            progressFill.style.width = '0%';
            progressBar.setAttribute('aria-valuenow', '0');
        }
    }

    function setProgress(percent) {
        const clamped = Math.max(0, Math.min(100, Math.round(percent)));
        progressFill.style.width = clamped + '%';
        progressBar.setAttribute('aria-valuenow', String(clamped));
        progressLabel.textContent = clamped + '%';
    }

    function postFormData(formData, onProgress) {
        return new Promise(function (resolve, reject) {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', '/api/upload', true);
            xhr.responseType = 'json';

            xhr.upload.addEventListener('progress', function (e) {
                if (e.lengthComputable && onProgress) {
                    onProgress((e.loaded / e.total) * 100);
                }
            });

            xhr.addEventListener('load', function () {
                let body = xhr.response;

                // responseType 'json' hone par responseText access THROWS
                // (InvalidStateError) — isliye guard zaroori hai.
                if (!body && xhr.responseType !== 'json') {
                    try { body = JSON.parse(xhr.responseText); } catch (_) { body = null; }
                }

                if (xhr.status >= 200 && xhr.status < 300) {
                    resolve(body || {});
                } else {
                    const message = (body && body.message)
                        ? body.message
                        : 'Upload failed (HTTP ' + xhr.status + ').';
                    reject(new Error(message));
                }
            });

            xhr.addEventListener('error', function () {
                reject(new Error('Network error — check your connection.'));
            });

            xhr.addEventListener('abort', function () {
                reject(new Error('Upload cancelled.'));
            });

            xhr.send(formData);
        });
    }

    async function uploadFilesSequentially(files, expiryMinutes, uploaderName) {
        setBusy(true);
        setProgress(0);

        let done = 0;
        let lastResult = null;

        try {
            for (let i = 0; i < files.length; i++) {
                const form = new FormData();
                form.append('file', files[i], files[i].name);
                form.append('expiryMinutes', String(expiryMinutes));
                form.append('uploaderName', uploaderName);

                lastResult = await postFormData(form, function (filePercent) {
                    setProgress(((done + filePercent / 100) / files.length) * 100);
                });

                done++;
            }

            setProgress(100);
            showResult(lastResult, files.length);
            clearSelection();
            refreshList();

        } catch (err) {
            toast(err.message, 'error');
        } finally {
            setBusy(false);
        }
    }

    async function uploadText(text, expiryMinutes, uploaderName) {
        setBusy(true);

        const form = new FormData();
        form.append('text', text);
        form.append('expiryMinutes', String(expiryMinutes));
        form.append('uploaderName', uploaderName);

        setProgress(30);

        try {
            const result = await postFormData(form, function (p) { setProgress(p); });
            setProgress(100);
            showResult(result, 1);
            textInput.value = '';
            charCount.textContent = '0';
            refreshList();

        } catch (err) {
            toast(err.message, 'error');
        } finally {
            setBusy(false);
        }
    }

    function clearSelection() {
        selectedFiles = [];
        renderSelectedFiles();
    }

    // =====================================================================
    // RESULT PANEL
    // =====================================================================
    function showResult(result, count) {
        resultText.textContent = result.message
            || (count > 1 ? count + ' files shared.' : 'Shared successfully.');

        codeValue.textContent = result.code || '------';
        resultBox.hidden = false;

        if (result.code) {
            toast('Shared! Code: ' + result.code, 'success');
        } else {
            toast('Shared successfully!', 'success');
        }
    }

    copyCodeBtn.addEventListener('click', function () {
        const code = codeValue.textContent;
        if (!code || code === '------') return;
        copyToClipboard(code, 'Code copied: ' + code);
    });

    /** Clipboard API sirf secure context pe chalta hai — fallback zaroori */
    async function copyToClipboard(text, successMessage) {
        if (navigator.clipboard && window.isSecureContext) {
            try {
                await navigator.clipboard.writeText(text);
                toast(successMessage, 'success');
                return;
            } catch (_) { }
        }

        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '-1000px';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();

        let ok = false;
        try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
        document.body.removeChild(ta);

        if (ok) {
            toast(successMessage, 'success');
        } else {
            toast('Copy failed — select it manually.', 'error');
        }
    }

    // =====================================================================
    // POLLING
    // =====================================================================
    async function refreshList() {
        try {
            const response = await fetch('/api/list', {
                headers: { 'Accept': 'application/json' },
                cache: 'no-store'
            });

            if (!response.ok) throw new Error('HTTP ' + response.status);

            const data = await response.json();

            if (data.serverTimeUtc) {
                clockOffsetMs = new Date(data.serverTimeUtc).getTime() - Date.now();
            }

            networkIpLabel.textContent = data.networkIp || 'unknown';
            renderList(data.items || []);

        } catch (err) {
            console.warn('[AirShare] list refresh failed:', err);
        }
    }

    function startPolling() {
        stopPolling();
        refreshList();
        pollTimer = setInterval(refreshList, POLL_INTERVAL_MS);
    }

    function stopPolling() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }

    // =====================================================================
    // RENDER LIST
    // =====================================================================
    function renderList(items) {
        shareList.setAttribute('aria-busy', 'true');

        const incomingIds = new Set(items.map(function (i) { return i.id; }));

        rendered.forEach(function (entry, id) {
            if (!incomingIds.has(id)) {
                rendered.delete(id);
                entry.root.classList.add('is-leaving');
                setTimeout(function () { entry.root.remove(); }, 260);
            }
        });

        items.forEach(function (item) {
            if (rendered.has(item.id)) {
                updateEntry(rendered.get(item.id), item);
                return;
            }
            const entry = buildEntry(item);
            rendered.set(item.id, entry);
            shareList.appendChild(entry.root);
            flash(entry.root, 'added');
        });

        items.forEach(function (item) {
            const entry = rendered.get(item.id);
            if (entry) updateEntry(entry, item);
        });

        // Empty state + count badge
        emptyState.classList.toggle('is-visible', items.length === 0);
        shareList.hidden = items.length === 0;

        listCount.textContent = items.length + (items.length === 1 ? ' item' : ' items');
        listCount.hidden = items.length === 0;

        setTimeout(function () { shareList.setAttribute('aria-busy', 'false'); }, 50);
    }

    function flash(element, type) {
        element.style.transition = 'background-color .2s ease';
        element.style.backgroundColor = type === 'added'
            ? 'color-mix(in srgb, var(--green) 16%, var(--surface-2))'
            : 'color-mix(in srgb, var(--red) 16%, var(--surface-2))';

        setTimeout(function () { element.style.backgroundColor = ''; }, 700);
    }

    function buildEntry(item) {
        const root = el('li', 'share-item');

        // ---- icon ----
        const icon = el('span', 'si-icon' + (item.type === 'text' ? ' type-text' : ''));
        icon.innerHTML = item.type === 'text'
            ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" '
            + 'stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V4h16v3M9 20h6M12 4v16"/></svg>'
            : '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" '
            + 'stroke-linecap="round" stroke-linejoin="round">'
            + '<path d="M21.44 11.05 12.25 20.2a5.5 5.5 0 0 1-7.78-7.78l9.19-9.19a3.67 3.67 0 1 1 5.18 5.18l-9.2 9.2a1.83 1.83 0 1 1-2.59-2.6l8.49-8.48"/></svg>';

        // ---- body ----
        const body = el('div', 'si-body');

        const name = el('div', 'si-name',
            item.fileName || (item.type === 'text' ? 'Shared text' : 'file'));
        name.title = item.fileName || '';

        const meta = el('div', 'si-meta');

        if (item.type === 'file' && item.fileSize != null) {
            meta.appendChild(el('span', null, formatBytes(item.fileSize)));
            meta.appendChild(el('span', 'si-sep', '•'));
        }

        const countdown = el('span', 'si-countdown', '--:--');
        meta.appendChild(countdown);

        if (item.code) {
            meta.appendChild(el('span', 'si-sep', '•'));
            meta.appendChild(el('span', 'si-code', item.code));
        }

        body.appendChild(name);
        body.appendChild(meta);

        // ---- uploader row: name + IP ----
        const uploader = el('div', 'si-uploader');

        const avatarTag = el('span', 'si-mini-avatar',
            (item.uploaderName || 'Unnamed').slice(0, 2).toUpperCase());
        uploader.appendChild(avatarTag);

        uploader.appendChild(el('span', 'si-name-tag', item.uploaderName || 'Unnamed'));

        if (item.networkIp) {
            uploader.appendChild(el('span', 'si-sep', '·'));
            uploader.appendChild(el('span', 'si-ip-tag', item.networkIp));
        }

        body.appendChild(uploader);

        let preview = null;
        if (item.type === 'text' && item.textPreview) {
            preview = el('div', 'si-preview', item.textPreview);
            body.appendChild(preview);
        }

        // ---- actions ----
        const actions = el('div', 'si-actions');

        if (item.type === 'file') {
            actions.appendChild(actionButton('Download', 'btn-accent', function () {
                downloadShare(item.id, item.fileName);
            }));
        } else {
            actions.appendChild(actionButton('Copy', 'btn-accent', function () {
                copyShareText(item.id);
            }));
        }

        if (item.code) {
            actions.appendChild(actionButton('Copy code', 'btn-ghost', function () {
                copyToClipboard(item.code, 'Code copied: ' + item.code);
            }));
        }

        actions.appendChild(actionButton('Delete', 'btn-danger', function () {
            deleteShare(item.id, root);
        }));

        root.appendChild(icon);
        root.appendChild(body);
        root.appendChild(actions);

        return { root: root, countdown: countdown, uploader: uploader, item: item };
    }

    function updateEntry(entry, item) {
        entry.item = item;
        entry.countdown.dataset.expires = item.expiresAtUtc;
        entry.countdown.textContent = formatRemaining(remainingMs(item.expiresAtUtc));

        const nameTag = entry.uploader.querySelector('.si-name-tag');
        if (nameTag) nameTag.textContent = item.uploaderName || 'Unnamed';

        const avatarTag = entry.uploader.querySelector('.si-mini-avatar');
        if (avatarTag) {
            avatarTag.textContent =
                (item.uploaderName || 'Unnamed').slice(0, 2).toUpperCase();
        }
    }

    function actionButton(label, extraClass, handler) {
        const btn = el('button', 'btn ' + extraClass, label);
        btn.type = 'button';
        btn.addEventListener('click', handler);
        return btn;
    }

    // =====================================================================
    // ITEM ACTIONS
    // =====================================================================
    async function downloadShare(id, fileName) {
        try {
            const response = await fetch('/api/download/' + encodeURIComponent(id));

            if (!response.ok) {
                let message = 'Download failed.';
                try {
                    const err = await response.json();
                    if (err && err.message) message = err.message;
                } catch (_) { }
                toast(message, 'error');
                return;
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);

            const a = document.createElement('a');
            a.href = url;
            a.download = fileName || 'download';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);

            setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
            toast('Downloading: ' + (fileName || 'file'), 'success');

        } catch (err) {
            toast('Download failed: ' + err.message, 'error');
        }
    }

    async function copyShareText(id) {
        try {
            const response = await fetch('/api/text/' + encodeURIComponent(id));
            const data = await response.json();

            if (!response.ok || !data.success) {
                toast((data && data.message) || 'Could not fetch text.', 'error');
                return;
            }

            copyToClipboard(data.text, 'Text copied.');

        } catch (err) {
            toast('Could not fetch text: ' + err.message, 'error');
        }
    }

    async function deleteShare(id, rootElement) {
        try {
            const response = await fetch('/api/share/' + encodeURIComponent(id), {
                method: 'DELETE',
                headers: { 'Accept': 'application/json' }
            });

            const data = await response.json().catch(function () { return null; });

            if (!response.ok || !data || !data.success) {
                toast((data && data.message) || 'Delete failed.', 'error');
                return;
            }

            flash(rootElement, 'removed');
            toast('Deleted.', 'success');
            refreshList();

        } catch (err) {
            toast('Delete failed: ' + err.message, 'error');
        }
    }

    // =====================================================================
    // COUNTDOWN
    // =====================================================================
    function serverNow() {
        return Date.now() + clockOffsetMs;
    }

    function remainingMs(expiresAtUtc) {
        return new Date(expiresAtUtc).getTime() - serverNow();
    }

    function tickCountdowns() {
        rendered.forEach(function (entry) {
            const expires = entry.countdown.dataset.expires;
            if (!expires) return;

            const ms = remainingMs(expires);

            if (ms <= 0) {
                entry.countdown.textContent = 'expired';
                entry.countdown.classList.add('is-urgent');
                return;
            }

            entry.countdown.textContent = formatRemaining(ms);
            entry.countdown.classList.toggle('is-urgent', ms < 120000);
        });
    }

    function formatRemaining(ms) {
        const totalSeconds = Math.floor(ms / 1000);
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const seconds = totalSeconds % 60;

        if (hours > 0) {
            return hours + 'h ' + String(minutes).padStart(2, '0') + 'm';
        }

        return String(minutes).padStart(2, '0') + ':'
            + String(seconds).padStart(2, '0');
    }

    // =====================================================================
    // HELPERS
    // =====================================================================

    /** Element banao. textContent use hota hai — XSS safe. */
    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function formatBytes(bytes) {
        if (bytes == null) return '';
        if (bytes < 1024) return bytes + ' B';

        const units = ['KB', 'MB', 'GB'];
        let value = bytes / 1024;
        let unit = 0;

        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }

        return (value >= 10 ? value.toFixed(0) : value.toFixed(1))
            + ' ' + units[unit];
    }

    // =====================================================================
    // TOASTS
    // =====================================================================
    const ICON_SUCCESS = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" '
        + 'stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">'
        + '<path d="M20 6 9 17l-5-5"/></svg>';

    const ICON_ERROR = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" '
        + 'stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">'
        + '<circle cx="12" cy="12" r="10"/><path d="M12 8v4M12 16h.01"/></svg>';

    function toast(message, kind) {
        const type = kind === 'error' ? 'error' : 'success';

        const node = el('div', 'toast toast-' + type);
        node.insertAdjacentHTML('afterbegin',
            type === 'error' ? ICON_ERROR : ICON_SUCCESS);

        node.appendChild(el('span', null, message));
        toastWrap.appendChild(node);

        setTimeout(function () {
            node.classList.add('is-out');
            setTimeout(function () { node.remove(); }, 240);
        }, type === 'error' ? 5000 : 3200);
    }

    // =====================================================================
    // INIT
    // =====================================================================
    loadName();
    switchTab('file');
    startPolling();
    tickTimer = setInterval(tickCountdowns, 1000);

    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            stopPolling();
        } else {
            startPolling();
        }
    });

    window.addEventListener('online', function () {
        toast('Connection restored.', 'success');
        refreshList();
    });
})();
