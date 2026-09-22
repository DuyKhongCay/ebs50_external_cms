/**
 * Mobile Client Engine for EBS-50 E-Tag CMS Handheld View.
 * Implements real-time tag management, image preview, state updates,
 * debounced search, visibility-aware polling, and touch-optimized flows.
 */

(() => {
    'use strict';

    // Application State
    const state = {
        allTags: [],
        filteredTags: [],
        statesCatalog: [],
        modelsCatalog: [],
        currentSearch: '',
        currentModelFilter: '',
        currentStateFilter: '',
        currentSyncFilter: '',
        activeTagForAction: null,
        selectedNewStateCode: null,
        isPollingActive: true,
        pollingTimerId: null,
        pollingIntervalMs: 5000,
        abortController: null,
        isFetching: false,
        isOffline: false
    };

    // UI Elements
    let elements = {};

    // Bootstrap Modal Instances
    let modalInstances = {};

    /**
     * Entry point called when DOM is ready.
     */
    window.initMobilePage = async () => {
        cacheElements();
        initModals();
        bindEvents();
        setupNetworkListeners();

        // Initial loading of catalogs and tags
        await Promise.allSettled([
            loadStatesCatalog(),
            loadModelsCatalog()
        ]);

        await loadTags(true);
        startPolling();
    };

    /**
     * Cache frequently accessed DOM nodes.
     */
    function cacheElements() {
        elements = {
            searchInput: document.getElementById('searchInput'),
            btnClearSearch: document.getElementById('btnClearSearch'),
            btnToggleFilters: document.getElementById('btnToggleFilters'),
            filterToggleText: document.getElementById('filterToggleText'),
            filterPanel: document.getElementById('filterPanel'),
            filterModel: document.getElementById('filterModel'),
            filterState: document.getElementById('filterState'),
            filterSync: document.getElementById('filterSync'),
            tagsContainer: document.getElementById('tagsContainer'),
            tagsCounterText: document.getElementById('tagsCounterText'),
            syncIndicator: document.getElementById('syncIndicator'),
            lastUpdatedText: document.getElementById('lastUpdatedText'),
            emptyState: document.getElementById('emptyState'),
            errorState: document.getElementById('errorState'),
            errorMessageText: document.getElementById('errorMessageText'),
            offlineBanner: document.getElementById('offlineBanner'),
            mobileAlertBox: document.getElementById('mobileAlertBox'),
            mobileAlertText: document.getElementById('mobileAlertText'),

            // Preview Modal
            modalPreviewEl: document.getElementById('modalPreview'),
            previewImage: document.getElementById('previewImage'),
            previewSpinner: document.getElementById('previewSpinner'),
            previewErrorText: document.getElementById('previewErrorText'),
            previewMachineNo: document.getElementById('previewMachineNo'),
            previewModelCode: document.getElementById('previewModelCode'),
            previewMac: document.getElementById('previewMac'),
            previewState: document.getElementById('previewState'),

            // Change State Modal
            modalChangeStateEl: document.getElementById('modalChangeState'),
            changeStateMachineNo: document.getElementById('changeStateMachineNo'),
            changeStateModel: document.getElementById('changeStateModel'),
            changeStateMac: document.getElementById('changeStateMac'),
            stateOptionsList: document.getElementById('stateOptionsList'),
            changeStateError: document.getElementById('changeStateError'),
            btnSubmitState: document.getElementById('btnSubmitState'),
            spinnerSubmitState: document.getElementById('spinnerSubmitState'),

            // Link Tag Modal
            modalLinkTagEl: document.getElementById('modalLinkTag'),
            formLinkTag: document.getElementById('formLinkTag'),
            linkMacAddress: document.getElementById('linkMacAddress'),
            linkMachineNo: document.getElementById('linkMachineNo'),
            linkModelCodeSelect: document.getElementById('linkModelCodeSelect'),
            linkCustomModel: document.getElementById('linkCustomModel'),
            linkInitialState: document.getElementById('linkInitialState'),
            linkAutoDispatch: document.getElementById('linkAutoDispatch'),
            existingMacNotice: document.getElementById('existingMacNotice'),
            linkFormError: document.getElementById('linkFormError'),
            btnSubmitLink: document.getElementById('btnSubmitLink'),
            spinnerSubmitLink: document.getElementById('spinnerSubmitLink'),

            // Unlink Modal
            modalConfirmUnlinkEl: document.getElementById('modalConfirmUnlink'),
            unlinkMachineNo: document.getElementById('unlinkMachineNo'),
            unlinkMac: document.getElementById('unlinkMac'),
            unlinkError: document.getElementById('unlinkError'),
            btnConfirmUnlink: document.getElementById('btnConfirmUnlink'),
            spinnerUnlink: document.getElementById('spinnerUnlink')
        };
    }

    /**
     * Initialize Bootstrap Modal objects.
     */
    function initModals() {
        if (window.bootstrap) {
            modalInstances.preview = new bootstrap.Modal(elements.modalPreviewEl);
            modalInstances.changeState = new bootstrap.Modal(elements.modalChangeStateEl);
            modalInstances.linkTag = new bootstrap.Modal(elements.modalLinkTagEl);
            modalInstances.unlink = new bootstrap.Modal(elements.modalConfirmUnlinkEl);
        }
    }

    /**
     * Wire DOM events and touch listeners.
     */
    function bindEvents() {
        // Debounced search input (300ms)
        let searchTimer = null;
        elements.searchInput.addEventListener('input', (e) => {
            const val = e.target.value.trim();
            elements.btnClearSearch.classList.toggle('d-none', val.length === 0);

            if (searchTimer) clearTimeout(searchTimer);
            searchTimer = setTimeout(() => {
                state.currentSearch = val;
                loadTags(false);
            }, 300);
        });

        // Clear search button
        window.clearSearchInput = () => {
            elements.searchInput.value = '';
            elements.btnClearSearch.classList.add('d-none');
            state.currentSearch = '';
            loadTags(false);
        };

        // Toggle advanced filter panel
        window.toggleFilterPanel = () => {
            const isHidden = elements.filterPanel.classList.contains('d-none');
            if (isHidden) {
                elements.filterPanel.classList.remove('d-none');
                elements.filterToggleText.textContent = 'Thu gọn ▴';
            } else {
                elements.filterPanel.classList.add('d-none');
                elements.filterToggleText.textContent = 'Mở rộng ▾';
            }
        };

        // Filter triggers
        window.applyFilters = () => {
            state.currentModelFilter = elements.filterModel.value;
            state.currentStateFilter = elements.filterState.value;
            state.currentSyncFilter = elements.filterSync.value;
            loadTags(false);
        };

        // Reset filter values
        window.resetFilters = () => {
            elements.filterModel.value = '';
            elements.filterState.value = '';
            elements.filterSync.value = '';
            elements.searchInput.value = '';
            elements.btnClearSearch.classList.add('d-none');

            state.currentSearch = '';
            state.currentModelFilter = '';
            state.currentStateFilter = '';
            state.currentSyncFilter = '';

            loadTags(false);
        };

        // Pause/resume polling when tab visibility changes to save battery and network
        document.addEventListener('visibilitychange', () => {
            if (document.hidden) {
                stopPolling();
            } else {
                startPolling();
                loadTags(false);
            }
        });
    }

    /**
     * Network status events (online / offline).
     */
    function setupNetworkListeners() {
        window.addEventListener('online', () => {
            setOfflineState(false);
            showMobileAlert('Đã khôi phục kết nối mạng.', 'success');
            loadTags(false);
        });

        window.addEventListener('offline', () => {
            setOfflineState(true);
        });
    }

    function setOfflineState(isOffline) {
        state.isOffline = isOffline;
        if (elements.offlineBanner) {
            elements.offlineBanner.classList.toggle('d-none', !isOffline);
        }
    }

    /**
     * Bottom Navigation Bar Handler
     */
    window.handleNavTab = (tab) => {
        document.querySelectorAll('.nav-item-btn').forEach(b => b.classList.remove('active'));

        if (tab === 'list') {
            document.getElementById('btnNavList')?.classList.add('active');
            window.scrollTo({ top: 0, behavior: 'smooth' });
        } else if (tab === 'link') {
            document.getElementById('btnNavLink')?.classList.add('active');
            window.openLinkTagModal();
        } else if (tab === 'refresh') {
            document.getElementById('btnNavRefresh')?.classList.add('active');
            loadTags(false);
            setTimeout(() => {
                document.getElementById('btnNavRefresh')?.classList.remove('active');
                document.getElementById('btnNavList')?.classList.add('active');
            }, 600);
        }
    };

    /**
     * Load Machine States catalog from API for quick display lookup.
     */
    async function loadStatesCatalog() {
        try {
            const resp = await fetch('/api/states');
            if (resp.ok) {
                const json = await resp.json();
                state.statesCatalog = json.data || [];
            }
        } catch (err) {
            console.warn('Unable to preload states catalog:', err);
        }
    }

    /**
     * Load Product Models catalog from API.
     */
    async function loadModelsCatalog() {
        try {
            const resp = await fetch('/api/models');
            if (resp.ok) {
                const json = await resp.json();
                state.modelsCatalog = json.data || [];
            }
        } catch (err) {
            console.warn('Unable to preload models catalog:', err);
        }
    }

    /**
     * Fetch Tags with search & filter parameters.
     */
    async function loadTags(showSkeleton = false) {
        if (state.isFetching && state.abortController) {
            state.abortController.abort();
        }

        state.abortController = new AbortController();
        const signal = state.abortController.signal;

        if (showSkeleton) {
            renderSkeletonLoading();
        }

        elements.syncIndicator.classList.remove('d-none');
        state.isFetching = true;

        try {
            const params = new URLSearchParams();
            if (state.currentSearch) params.append('search', state.currentSearch);
            if (state.currentModelFilter) params.append('model', state.currentModelFilter);
            if (state.currentStateFilter) params.append('state', state.currentStateFilter);

            const url = `/api/tags${params.toString() ? '?' + params.toString() : ''}`;
            const response = await fetch(url, { signal });

            if (response.status === 503) {
                showMobileAlert('Máy chủ đang trong chế độ bảo trì cơ sở dữ liệu. Vui lòng chờ...', 'warning');
                return;
            }

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: Lỗi máy chủ.`);
            }

            const result = await response.json();
            let tags = result.data || [];

            // Client-side filter by sync status if selected
            if (state.currentSyncFilter) {
                tags = tags.filter(t => (t.syncStatus || '').toLowerCase() === state.currentSyncFilter.toLowerCase());
            }

            state.allTags = tags;
            setOfflineState(false);
            renderTagsList(tags);

            // Update timestamp
            const now = new Date();
            elements.lastUpdatedText.textContent = `Cập nhật: ${now.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
            elements.errorState.classList.add('d-none');
        } catch (err) {
            if (err.name === 'AbortError') {
                return; // Normal cancellation due to newer request
            }
            console.error('Error loading tags:', err);
            if (!navigator.onLine) {
                setOfflineState(true);
            } else if (state.allTags.length === 0) {
                elements.errorMessageText.textContent = err.message || 'Không thể nạp dữ liệu từ máy chủ.';
                elements.errorState.classList.remove('d-none');
                elements.tagsContainer.innerHTML = '';
            }
        } finally {
            state.isFetching = false;
            elements.syncIndicator.classList.add('d-none');
        }
    }

    window.retryLoadTags = () => {
        loadTags(true);
    };

    /**
     * Render tag cards list into container.
     */
    function renderTagsList(tags) {
        elements.tagsContainer.innerHTML = '';
        elements.tagsCounterText.textContent = `Tổng số: ${tags.length} thẻ`;

        if (!tags || tags.length === 0) {
            elements.emptyState.classList.remove('d-none');
            return;
        }

        elements.emptyState.classList.add('d-none');

        const fragment = document.createDocumentFragment();

        tags.forEach(tag => {
            const card = createTagCardElement(tag);
            fragment.appendChild(card);
        });

        elements.tagsContainer.appendChild(fragment);
    }

    /**
     * Construct DOM elements for a single tag card securely.
     */
    function createTagCardElement(tag) {
        const card = document.createElement('article');
        card.className = 'tag-card p-3';
        card.id = `tag-card-${sanitizeId(tag.macAddress)}`;

        // Card border color indicator based on state theme color
        const themeColor = tag.themeColor || '#0d6efd';
        card.style.borderLeft = `5px solid ${escapeHtml(themeColor)}`;

        // Top Row: MachineNo & Battery + Sync status
        const topRow = document.createElement('div');
        topRow.className = 'd-flex justify-content-between align-items-center mb-2';

        const machineTitle = document.createElement('div');
        machineTitle.className = 'card-machine-no d-flex align-items-center gap-1';
        machineTitle.innerHTML = `<span>🏷️</span> <span>Máy: ${escapeHtml(tag.machineNo)}</span>`;

        const badgesCol = document.createElement('div');
        badgesCol.className = 'd-flex align-items-center gap-1';

        // Battery indicator
        const batteryBadge = document.createElement('span');
        const batteryClass = getBatteryBadgeClass(tag.batteryLevel);
        batteryBadge.className = `battery-badge ${batteryClass}`;
        batteryBadge.textContent = `🔋 ${tag.batteryLevel}%`;

        // Sync status badge
        const syncBadge = createSyncBadgeElement(tag.syncStatus);

        badgesCol.appendChild(batteryBadge);
        badgesCol.appendChild(syncBadge);

        topRow.appendChild(machineTitle);
        topRow.appendChild(badgesCol);

        // Details Section
        const detailsRow = document.createElement('div');
        detailsRow.className = 'info-row mb-2';

        const modelAndMac = document.createElement('div');
        modelAndMac.className = 'd-flex justify-content-between text-muted';
        modelAndMac.innerHTML = `
            <span>Model: <strong>${escapeHtml(tag.modelCode)}</strong></span>
            <span class="font-monospace text-break">MAC: <strong>${escapeHtml(tag.macAddress)}</strong></span>
        `;

        const stateInfo = document.createElement('div');
        stateInfo.className = 'mt-1 d-flex align-items-center gap-1';
        stateInfo.innerHTML = `
            <span class="text-muted">Trạng thái:</span>
            <span class="state-badge" style="background-color: ${escapeHtml(themeColor)}20; color: #000; border: 1px solid ${escapeHtml(themeColor)}">
                [${tag.stateCode}] ${escapeHtml(tag.stateNameVi || 'Chưa định nghĩa')}
            </span>
        `;

        const syncTimeInfo = document.createElement('div');
        syncTimeInfo.className = 'text-muted mt-1';
        syncTimeInfo.style.fontSize = '0.75rem';
        const syncTimeStr = tag.lastSyncedAt ? formatDateTime(tag.lastSyncedAt) : 'Chưa đồng bộ';
        syncTimeInfo.textContent = `Đồng bộ lần cuối: ${syncTimeStr}`;

        detailsRow.appendChild(modelAndMac);
        detailsRow.appendChild(stateInfo);
        detailsRow.appendChild(syncTimeInfo);

        // Actions Row
        const actionsRow = document.createElement('div');
        actionsRow.className = 'd-flex gap-2 pt-2 border-top align-items-center';

        // Button 1: Preview Image
        const btnPreview = document.createElement('button');
        btnPreview.type = 'button';
        btnPreview.className = 'btn btn-outline-dark btn-sm btn-touch flex-grow-1 fw-semibold';
        btnPreview.innerHTML = '🖼️ Xem ảnh';
        btnPreview.onclick = () => openPreviewModal(tag);

        // Button 2: Change State
        const btnState = document.createElement('button');
        btnState.type = 'button';
        btnState.className = 'btn btn-primary btn-sm btn-touch flex-grow-1 fw-semibold';
        btnState.innerHTML = '🔄 Đổi trạng thái';
        btnState.onclick = () => openChangeStateModal(tag);

        // Button 3: Dropdown More
        const dropdownWrap = document.createElement('div');
        dropdownWrap.className = 'dropdown';

        const btnMore = document.createElement('button');
        btnMore.type = 'button';
        btnMore.className = 'btn btn-outline-secondary btn-sm btn-touch px-2';
        btnMore.setAttribute('data-bs-toggle', 'dropdown');
        btnMore.setAttribute('aria-expanded', 'false');
        btnMore.innerHTML = '⋮';

        const dropdownMenu = document.createElement('ul');
        dropdownMenu.className = 'dropdown-menu dropdown-menu-end shadow-sm';

        const itemEdit = document.createElement('li');
        const linkEdit = document.createElement('a');
        linkEdit.className = 'dropdown-item py-2';
        linkEdit.href = '#';
        linkEdit.innerHTML = '✏️ Sửa ghép nối';
        linkEdit.onclick = (e) => {
            e.preventDefault();
            openEditTagModal(tag);
        };
        itemEdit.appendChild(linkEdit);

        const itemDelete = document.createElement('li');
        const linkDelete = document.createElement('a');
        linkDelete.className = 'dropdown-item py-2 text-danger';
        linkDelete.href = '#';
        linkDelete.innerHTML = '🗑️ Hủy ghép nối';
        linkDelete.onclick = (e) => {
            e.preventDefault();
            openUnlinkModal(tag);
        };
        itemDelete.appendChild(linkDelete);

        dropdownMenu.appendChild(itemEdit);
        dropdownMenu.appendChild(itemDelete);

        dropdownWrap.appendChild(btnMore);
        dropdownWrap.appendChild(dropdownMenu);

        actionsRow.appendChild(btnPreview);
        actionsRow.appendChild(btnState);
        actionsRow.appendChild(dropdownWrap);

        // Assemble card
        card.appendChild(topRow);
        card.appendChild(detailsRow);
        card.appendChild(actionsRow);

        return card;
    }

    /**
     * Map SyncStatus to localized labels and Bootstrap badges.
     */
    function createSyncBadgeElement(status) {
        const badge = document.createElement('span');
        const s = (status || '').toLowerCase();

        switch (s) {
            case 'pending':
                badge.className = 'sync-badge bg-secondary text-white';
                badge.textContent = 'Chờ gửi';
                break;
            case 'dispatching':
                badge.className = 'sync-badge bg-info text-dark';
                badge.textContent = 'Đang gửi';
                break;
            case 'uploaded':
                badge.className = 'sync-badge bg-primary text-white';
                badge.textContent = 'Đã tải lên EBS';
                break;
            case 'confirmed':
            case 'synced':
                badge.className = 'sync-badge bg-success text-white';
                badge.textContent = 'Đã xác nhận';
                break;
            case 'error':
                badge.className = 'sync-badge bg-danger text-white';
                badge.textContent = 'Lỗi đồng bộ';
                break;
            default:
                badge.className = 'sync-badge bg-dark text-white';
                badge.textContent = status ? `Trạng thái: ${status}` : 'Chưa gửi';
                break;
        }

        return badge;
    }

    function getBatteryBadgeClass(level) {
        if (level > 50) return 'bg-success-subtle text-success border border-success-subtle';
        if (level > 20) return 'bg-warning-subtle text-warning-emphasis border border-warning-subtle';
        return 'bg-danger-subtle text-danger border border-danger-subtle';
    }

    /**
     * Render skeleton placeholders while fetching.
     */
    function renderSkeletonLoading() {
        elements.tagsContainer.innerHTML = `
            <div class="tag-card p-3">
                <div class="d-flex justify-content-between mb-2">
                    <div class="skeleton-box" style="width: 140px; height: 24px;"></div>
                    <div class="skeleton-box" style="width: 70px; height: 24px;"></div>
                </div>
                <div class="skeleton-box mb-2" style="width: 80%; height: 16px;"></div>
                <div class="skeleton-box mb-3" style="width: 50%; height: 16px;"></div>
                <div class="d-flex gap-2">
                    <div class="skeleton-box" style="flex: 1; height: 38px;"></div>
                    <div class="skeleton-box" style="flex: 1; height: 38px;"></div>
                </div>
            </div>
        `;
    }

    // ==========================================
    // MODAL FLOW 1: PREVIEW IMAGE
    // ==========================================

    window.openPreviewModal = (tag) => {
        state.activeTagForAction = tag;

        elements.previewMachineNo.textContent = tag.machineNo;
        elements.previewModelCode.textContent = tag.modelCode;
        elements.previewMac.textContent = tag.macAddress;
        elements.previewState.textContent = `[${tag.stateCode}] ${tag.stateNameVi || ''}`;

        elements.previewImage.classList.add('d-none');
        elements.previewSpinner.classList.remove('d-none');
        elements.previewErrorText.classList.add('d-none');

        // Add timestamp parameter to bypass browser caching when state changes
        const imageUrl = `/api/render/preview/${encodeURIComponent(tag.macAddress)}?t=${Date.now()}`;
        elements.previewImage.src = imageUrl;

        modalInstances.preview?.show();
    };

    window.onPreviewImageLoaded = () => {
        elements.previewSpinner.classList.add('d-none');
        elements.previewImage.classList.remove('d-none');
    };

    window.onPreviewImageError = () => {
        elements.previewSpinner.classList.add('d-none');
        elements.previewErrorText.classList.remove('d-none');
    };

    // ==========================================
    // MODAL FLOW 2: CHANGE MACHINE STATE
    // ==========================================

    window.openChangeStateModal = async (tag) => {
        state.activeTagForAction = tag;
        state.selectedNewStateCode = tag.stateCode;

        elements.changeStateMachineNo.textContent = tag.machineNo;
        elements.changeStateModel.textContent = tag.modelCode;
        elements.changeStateMac.textContent = tag.macAddress;
        elements.changeStateError.classList.add('d-none');
        elements.btnSubmitState.disabled = false;
        elements.spinnerSubmitState.classList.add('d-none');

        renderStateOptionsList(tag.stateCode);
        modalInstances.changeState?.show();

        // Optional fresh status fetch for this specific tag
        try {
            const res = await fetch(`/api/tags/${encodeURIComponent(tag.macAddress)}`);
            if (res.ok) {
                const fresh = await res.json();
                if (fresh.data) {
                    state.activeTagForAction = fresh.data;
                    renderStateOptionsList(fresh.data.stateCode);
                }
            }
        } catch (e) {
            // Non-critical, continue with cached info
        }
    };

    function renderStateOptionsList(currentStateCode) {
        elements.stateOptionsList.innerHTML = '';

        if (!state.statesCatalog || state.statesCatalog.length === 0) {
            elements.stateOptionsList.innerHTML = '<div class="text-muted small">Đang nạp danh mục trạng thái...</div>';
            return;
        }

        state.statesCatalog.forEach(st => {
            const item = document.createElement('div');
            const isCurrent = (st.stateCode === currentStateCode);
            const isSelected = (st.stateCode === state.selectedNewStateCode);

            item.className = `state-option-item d-flex justify-content-between align-items-center ${isSelected ? 'selected' : ''}`;
            item.onclick = () => {
                state.selectedNewStateCode = st.stateCode;
                renderStateOptionsList(currentStateCode);
            };

            const leftPart = document.createElement('div');
            leftPart.innerHTML = `
                <div class="fw-bold" style="color: ${escapeHtml(st.themeColor || '#000')}">
                    [${st.stateCode}] ${escapeHtml(st.stateNameVi)}
                </div>
                <small class="text-muted">${escapeHtml(st.stateNameKo || '')}</small>
            `;

            const rightPart = document.createElement('div');
            if (isCurrent) {
                rightPart.innerHTML = '<span class="badge bg-secondary">Hiện tại</span>';
            } else if (isSelected) {
                rightPart.innerHTML = '<span class="badge bg-primary">Đã chọn</span>';
            }

            item.appendChild(leftPart);
            item.appendChild(rightPart);
            elements.stateOptionsList.appendChild(item);
        });
    }

    window.submitChangeState = async () => {
        if (!state.activeTagForAction || state.selectedNewStateCode === null) return;

        const mac = state.activeTagForAction.macAddress;
        const newCode = state.selectedNewStateCode;

        elements.btnSubmitState.disabled = true;
        elements.spinnerSubmitState.classList.remove('d-none');
        elements.changeStateError.classList.add('d-none');

        try {
            const resp = await fetch(`/api/tags/${encodeURIComponent(mac)}/state`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ stateCode: parseInt(newCode, 10) })
            });

            const result = await resp.json();

            if (!resp.ok) {
                throw new Error(result.message || 'Cập nhật trạng thái thất bại.');
            }

            modalInstances.changeState?.hide();
            showMobileAlert(`Đã gửi lệnh đổi trạng thái [${newCode}] cho máy ${state.activeTagForAction.machineNo}. Đang đồng bộ...`, 'success');

            // Fast refresh to show Pending/Dispatching immediately
            await loadTags(false);
        } catch (err) {
            elements.changeStateError.textContent = err.message || 'Lỗi gửi yêu cầu đến máy chủ.';
            elements.changeStateError.classList.remove('d-none');
        } finally {
            elements.btnSubmitState.disabled = false;
            elements.spinnerSubmitState.classList.add('d-none');
        }
    };

    // ==========================================
    // MODAL FLOW 3: LINK / RELINK TAG
    // ==========================================

    window.openLinkTagModal = () => {
        document.getElementById('modalLinkTagTitle').textContent = '🔗 Ghép Thẻ E-Tag Mới';
        elements.formLinkTag.reset();
        elements.linkMacAddress.readOnly = false;
        elements.linkCustomModel.classList.add('d-none');
        elements.linkModelCodeSelect.value = '';
        elements.existingMacNotice.classList.add('d-none');
        elements.linkFormError.classList.add('d-none');
        elements.btnSubmitLink.disabled = false;
        elements.spinnerSubmitLink.classList.add('d-none');

        modalInstances.linkTag?.show();
    };

    window.openEditTagModal = (tag) => {
        document.getElementById('modalLinkTagTitle').textContent = `✏️ Sửa Ghép Nối Máy ${tag.machineNo}`;
        elements.linkMacAddress.value = tag.macAddress;
        elements.linkMacAddress.readOnly = true;
        elements.linkMachineNo.value = tag.machineNo;
        elements.linkInitialState.value = tag.stateCode;
        elements.linkAutoDispatch.checked = true;
        elements.existingMacNotice.classList.add('d-none');
        elements.linkFormError.classList.add('d-none');

        // Select model or setup custom
        const existingOption = Array.from(elements.linkModelCodeSelect.options).find(o => o.value === tag.modelCode);
        if (existingOption) {
            elements.linkModelCodeSelect.value = tag.modelCode;
            elements.linkCustomModel.classList.add('d-none');
        } else {
            elements.linkModelCodeSelect.value = '__CUSTOM__';
            elements.linkCustomModel.classList.remove('d-none');
            elements.linkCustomModel.value = tag.modelCode;
        }

        modalInstances.linkTag?.show();
    };

    window.handleModelSelectChange = () => {
        const val = elements.linkModelCodeSelect.value;
        if (val === '__CUSTOM__') {
            elements.linkCustomModel.classList.remove('d-none');
            elements.linkCustomModel.focus();
        } else {
            elements.linkCustomModel.classList.add('d-none');
            elements.linkCustomModel.value = '';
        }
    };

    window.checkExistingMacOnBlur = () => {
        const mac = elements.linkMacAddress.value.trim().toUpperCase();
        elements.linkMacAddress.value = mac;

        if (mac.length < 4) {
            elements.existingMacNotice.classList.add('d-none');
            return;
        }

        const existing = state.allTags.find(t => t.macAddress.toUpperCase() === mac);
        if (existing) {
            elements.existingMacNotice.textContent = `ℹ️ MAC này hiện đã ghép với Máy "${existing.machineNo}". Việc lưu sẽ cập nhật ghép nối sang máy mới.`;
            elements.existingMacNotice.classList.remove('d-none');
        } else {
            elements.existingMacNotice.classList.add('d-none');
        }
    };

    window.submitLinkTag = async () => {
        const mac = elements.linkMacAddress.value.trim().toUpperCase();
        const machineNo = elements.linkMachineNo.value.trim();
        let modelCode = elements.linkModelCodeSelect.value;

        if (modelCode === '__CUSTOM__') {
            modelCode = elements.linkCustomModel.value.trim();
        }

        const initialStateCode = parseInt(elements.linkInitialState.value, 10) || 0;
        const autoDispatch = elements.linkAutoDispatch.checked;

        // Validations
        if (!mac || mac.length < 4 || mac.length > 32) {
            showFormError(elements.linkFormError, 'Địa chỉ MAC phải từ 4 đến 32 ký tự.');
            return;
        }
        if (!machineNo) {
            showFormError(elements.linkFormError, 'Vui lòng nhập Mã máy / Vị trí.');
            return;
        }
        if (!modelCode) {
            showFormError(elements.linkFormError, 'Vui lòng chọn hoặc nhập Model E-Tag.');
            return;
        }

        elements.btnSubmitLink.disabled = true;
        elements.spinnerSubmitLink.classList.remove('d-none');
        elements.linkFormError.classList.add('d-none');

        const payload = {
            macAddress: mac,
            machineNo: machineNo,
            modelCode: modelCode,
            initialStateCode: initialStateCode,
            autoDispatch: autoDispatch
        };

        try {
            const resp = await fetch('/api/tags/link', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            const resJson = await resp.json();

            if (!resp.ok) {
                throw new Error(resJson.message || 'Lỗi ghép thẻ.');
            }

            modalInstances.linkTag?.hide();
            showMobileAlert(resJson.message || `Đã ghép thẻ ${mac} cho máy ${machineNo} thành công!`, 'success');

            // Refresh catalogs in case new Model was created
            await loadModelsCatalog();
            await loadTags(false);
        } catch (err) {
            showFormError(elements.linkFormError, err.message || 'Lỗi gửi yêu cầu ghép nối.');
        } finally {
            elements.btnSubmitLink.disabled = false;
            elements.spinnerSubmitLink.classList.add('d-none');
        }
    };

    // ==========================================
    // MODAL FLOW 4: UNLINK TAG
    // ==========================================

    window.openUnlinkModal = (tag) => {
        state.activeTagForAction = tag;
        elements.unlinkMachineNo.textContent = tag.machineNo;
        elements.unlinkMac.textContent = tag.macAddress;
        elements.unlinkError.classList.add('d-none');
        elements.btnConfirmUnlink.disabled = false;
        elements.spinnerUnlink.classList.add('d-none');

        modalInstances.unlink?.show();
    };

    window.submitUnlinkTag = async () => {
        if (!state.activeTagForAction) return;

        const mac = state.activeTagForAction.macAddress;
        elements.btnConfirmUnlink.disabled = true;
        elements.spinnerUnlink.classList.remove('d-none');
        elements.unlinkError.classList.add('d-none');

        try {
            const resp = await fetch(`/api/tags/${encodeURIComponent(mac)}`, {
                method: 'DELETE'
            });

            const resJson = await resp.json();

            if (!resp.ok) {
                throw new Error(resJson.message || 'Hủy ghép nối thất bại.');
            }

            modalInstances.unlink?.hide();
            showMobileAlert(`Đã hủy ghép nối thẻ ${mac} thành công.`, 'success');

            // Remove card from DOM immediately then refresh
            const cardEl = document.getElementById(`tag-card-${sanitizeId(mac)}`);
            if (cardEl) cardEl.remove();

            await loadTags(false);
        } catch (err) {
            elements.unlinkError.textContent = err.message || 'Lỗi máy chủ khi hủy ghép.';
            elements.unlinkError.classList.remove('d-none');
        } finally {
            elements.btnConfirmUnlink.disabled = false;
            elements.spinnerUnlink.classList.add('d-none');
        }
    };

    // ==========================================
    // POLLING ENGINE
    // ==========================================

    function startPolling() {
        stopPolling();
        state.isPollingActive = true;

        const poll = async () => {
            if (!state.isPollingActive) return;

            // Do not poll if a full-screen modal is currently active to prevent jarring inputs
            const isAnyModalOpen = document.body.classList.contains('modal-open');
            if (!isAnyModalOpen && !document.hidden && !state.isFetching) {
                await loadTags(false);
            }

            if (state.isPollingActive) {
                state.pollingTimerId = setTimeout(poll, state.pollingIntervalMs);
            }
        };

        state.pollingTimerId = setTimeout(poll, state.pollingIntervalMs);
    }

    function stopPolling() {
        state.isPollingActive = false;
        if (state.pollingTimerId) {
            clearTimeout(state.pollingTimerId);
            state.pollingTimerId = null;
        }
    }

    // ==========================================
    // UTILITY HELPERS
    // ==========================================

    function showFormError(container, msg) {
        container.textContent = msg;
        container.classList.remove('d-none');
    }

    function showMobileAlert(message, type = 'info') {
        elements.mobileAlertText.textContent = message;
        elements.mobileAlertBox.className = `alert alert-${type} alert-dismissible fade show mb-3 shadow-sm`;
        elements.mobileAlertBox.classList.remove('d-none');

        window.scrollTo({ top: 0, behavior: 'smooth' });

        setTimeout(() => {
            elements.mobileAlertBox.classList.add('d-none');
        }, 5000);
    }

    window.hideMobileAlert = () => {
        elements.mobileAlertBox.classList.add('d-none');
    };

    function sanitizeId(str) {
        return (str || '').replace(/[^a-zA-Z0-9_-]/g, '_');
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function formatDateTime(isoStr) {
        try {
            const d = new Date(isoStr);
            if (isNaN(d.getTime())) return isoStr;
            return d.toLocaleString('vi-VN', {
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit',
                day: '2-digit',
                month: '2-digit',
                year: 'numeric'
            });
        } catch {
            return isoStr;
        }
    }

})();
