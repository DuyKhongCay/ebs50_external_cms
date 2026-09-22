(() => {
    const inventory = document.getElementById('network-interfaces');
    const message = document.getElementById('network-message');
    const diagnose = document.getElementById('network-diagnose');
    const cancel = document.getElementById('network-cancel');
    const refresh = document.getElementById('network-refresh');
    let pending;
    const element = (tag, text, parent) => {
        const node = document.createElement(tag);
        node.textContent = text;
        parent.appendChild(node);
        return node;
    };
    async function load() {
        refresh.disabled = true;
        try {
            const response = await fetch('/api/network/interfaces');
            if (!response.ok) throw new Error('Không đọc được thông tin mạng.');
            const interfaces = await response.json();
            inventory.replaceChildren();
            for (const nic of interfaces) {
                const column = element('div', '', inventory);
                column.className = 'col-md-6';
                const card = element('div', '', column);
                card.className = 'card card-body h-100';
                const role = { Office: 'Mạng văn phòng', EBS: 'Mạng EBS', Unassigned: 'Chưa gán vai trò' }[nic.role];
                element('h2', `${nic.name} — ${role}`, card).className = 'h5';
                element('p', `Trạng thái: ${nic.status} | IP: ${nic.addresses.join(', ') || 'Chưa có IPv4'}`, card);
                element('p', `Gateway: ${nic.gateways.join(', ') || 'Không có'}`, card);
                element('small', `Interface ID: ${nic.id}`, card);
                for (const url of nic.webUrls) {
                    const row = element('div', '', card);
                    const link = element('a', url, row);
                    link.href = url;
                    const copy = element('button', 'Sao chép', row);
                    copy.className = 'btn btn-sm btn-outline-secondary ms-2';
                    copy.addEventListener('click', async () => {
                        try { await navigator.clipboard.writeText(url); message.textContent = 'Đã sao chép URL.'; }
                        catch { message.textContent = `Hãy chọn và sao chép địa chỉ: ${url}`; }
                    });
                }
            }
            if (!interfaces.length) message.textContent = 'Không tìm thấy interface mạng.';
        } catch (error) { message.textContent = error.message; }
        finally { refresh.disabled = false; }
    }
    diagnose.addEventListener('click', async () => {
        pending = new AbortController();
        diagnose.disabled = true;
        cancel.disabled = false;
        message.textContent = 'Đang kiểm tra…';
        const body = document.getElementById('network-results');
        body.replaceChildren();
        try {
            const response = await fetch('/api/network/diagnostics', { method: 'POST', signal: pending.signal });
            if (!response.ok) throw new Error(response.status === 429 ? 'Đang có lượt kiểm tra khác. Vui lòng thử lại.' : 'Không hoàn tất được chẩn đoán.');
            const result = await response.json();
            for (const check of result.checks) {
                const row = element('tr', '', body);
                const status = { Passed: 'Thành công', Failed: 'Lỗi', TimedOut: 'Hết thời gian', Unknown: 'Chưa xác định' }[check.status] || check.status;
                for (const value of [check.name, check.target, status, `${check.message} (${check.code})${check.sourceAddress ? ` — Nguồn: ${check.sourceAddress}` : ''}`, `${check.durationMs} ms`]) element('td', value, row);
            }
            message.textContent = `Cập nhật: ${new Date(result.checkedAt).toLocaleString()}`;
        } catch (error) { message.textContent = error.name === 'AbortError' ? 'Đã hủy kiểm tra.' : error.message; }
        finally { pending = null; diagnose.disabled = false; cancel.disabled = true; }
    });
    cancel.addEventListener('click', () => pending?.abort());
    refresh.addEventListener('click', load);
    window.addEventListener('pagehide', () => pending?.abort());
    load();
})();
