(() => {
    'use strict';
    const element = id => document.getElementById(`database-${id}`);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const storageKey = 'ebs50-database-operation';
    let busy = false;
    let importId = null;
    let status = { maintenance: false, syncPaused: false };

    function controls() {
        for (const id of ['export', 'validate', 'file', 'confirm', 'resume']) {
            element(id).disabled = busy || status.maintenance;
        }
        element('restore').disabled = busy || status.maintenance || !importId || !element('confirm').checked;
    }

    async function request(path, options = {}) {
        const response = await fetch(`/api/database/${path}`, {
            ...options, headers: { 'X-CSRF-TOKEN': token, ...options.headers }, cache: 'no-store'
        });
        if (!response.ok) {
            let message = `Yêu cầu thất bại (HTTP ${response.status}).`;
            try {
                const body = await response.json();
                message = body.error || body.detail || body.title || message;
            } catch { /* A proxy or a body-size rejection may return a non-JSON response. */ }
            throw new Error(message);
        }
        return response.json();
    }

    async function refreshStatus() {
        status = await request('status');
        element('status').textContent = status.maintenance ? 'Database đang bảo trì; các truy cập dữ liệu tạm dừng.'
            : status.syncPaused ? 'Database sẵn sàng. Đồng bộ EBS đang tạm dừng sau khôi phục.' : 'Database và đồng bộ EBS sẵn sàng.';
        element('resume-panel').classList.toggle('d-none', !status.syncPaused);
        controls();
    }

    function showPreview(operation) {
        importId = operation.id;
        const preview = operation.preview;
        element('preview-info').textContent = `Backup: ${new Date(preview.createdAtUtc).toLocaleString()} · Phiên bản ${preview.applicationVersion} · Job chưa hoàn tất: ${preview.unfinishedJobCount} · Có thể khôi phục đến ${new Date(operation.expiresAtUtc).toLocaleString()}`;
        element('counts').replaceChildren();
        for (const [name, count] of Object.entries(preview.tableCounts)) {
            const row = document.createElement('tr');
            for (const value of [name, count]) {
                const cell = document.createElement('td');
                cell.textContent = value;
                row.appendChild(cell);
            }
            element('counts').appendChild(row);
        }
        element('confirm').checked = false;
        element('preview').classList.remove('d-none');
    }

    async function follow(id) {
        busy = true;
        controls();
        localStorage.setItem(storageKey, id);
        try {
            while (true) {
                const operation = await request(`operations/${id}`);
                element('message').textContent = operation.error || operation.stage;
                element('recovery').textContent = operation.recoveryBackup ? `Bản phục hồi trước import trên server: ${operation.recoveryBackup}` : '';
                if (operation.status === 'Failed') throw new Error(operation.error || operation.stage);
                if (operation.status === 'Succeeded') {
                    if (operation.kind === 'Export') {
                        element('download').href = `/api/database/exports/${id}/download`;
                        element('download').classList.remove('d-none');
                    }
                    if (operation.kind === 'Validate') showPreview(operation);
                    if (operation.kind === 'Restore') {
                        importId = null;
                        element('preview').classList.add('d-none');
                    }
                    break;
                }
                if (operation.status === 'Consumed') break;
                await new Promise(resolve => setTimeout(resolve, 1000));
            }
        } finally {
            busy = false;
            controls();
            await refreshStatus();
        }
    }

    async function start(path, body) {
        busy = true;
        controls();
        element('message').textContent = 'Đang gửi yêu cầu...';
        try {
            const operation = await request(path, { method: 'POST', body });
            await follow(operation.id);
        } catch (error) {
            element('message').textContent = error.message;
        } finally {
            busy = false;
            controls();
        }
    }

    element('export').addEventListener('click', () => {
        element('download').classList.add('d-none');
        void start('exports');
    });
    element('upload-form').addEventListener('submit', event => {
        event.preventDefault();
        const file = element('file').files[0];
        if (!file) return;
        if (file.size > 100 * 1024 * 1024) {
            element('message').textContent = 'File vượt giới hạn 100 MiB.';
            return;
        }
        importId = null;
        element('preview').classList.add('d-none');
        const form = new FormData();
        form.append('file', file);
        void start('imports/validate', form);
    });
    element('confirm').addEventListener('change', controls);
    element('restore').addEventListener('click', () => {
        if (importId && element('confirm').checked) void start(`imports/${importId}/restore`);
    });
    element('resume').addEventListener('click', () => {
        if (window.confirm('Tạo job mới cho tất cả tag và bắt đầu gửi dữ liệu hiện tại đến EBS-50?')) void start('resume-sync');
    });

    async function initialize() {
        try {
            await refreshStatus();
            const previous = localStorage.getItem(storageKey);
            if (previous && /^[0-9a-f-]{36}$/i.test(previous)) await follow(previous);
        } catch (error) {
            element('message').textContent = error.message;
        }
    }
    async function pollStatus() {
        try { await refreshStatus(); }
        catch (error) { element('status').textContent = error.message; }
        setTimeout(pollStatus, 5000);
    }
    void initialize();
    setTimeout(pollStatus, 5000);
})();
