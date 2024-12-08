// 影像管理的分页变量
let imagesCurrentPage = 1;
const imagesPageSize = 10;

// 初始化影像管理模块
function initializeImages() {
    try {
        // 绑定影像管理相关事件
        bindImagesEvents();
        
        // 加载初始数据
        loadImages();
    } catch (error) {
        console.error('初始化影像管理模块失败:', error);
        window.showToast('初始化影像管理模块失败', 'error');
    }
}

// 加载影像数据
async function loadImages(page = 1) {
    const tbody = document.getElementById('images-table-body');
    showTableLoading(tbody, 8);  // 影像列表有8列

    try {
        const patientId = document.getElementById('images-searchPatientId')?.value || '';
        const patientName = document.getElementById('images-searchPatientName')?.value || '';
        const accessionNumber = document.getElementById('images-searchAccessionNumber')?.value || '';
        const modality = document.getElementById('images-searchModality')?.value || '';
        const studyDate = document.getElementById('images-searchStudyDate')?.value || '';
        
        const params = new URLSearchParams({
            page,
            pageSize: imagesPageSize,
            patientId,
            patientName,
            accessionNumber,
            modality,
            studyDate
        });

        const response = await fetch(`/api/images?${params}`);
        if (!response.ok) {
            throw new Error('获取数据失败');
        }

        const result = await response.json();
        if (result.items.length === 0) {
            showEmptyTable(tbody, '暂无影像数据', 8);
            return;
        }

        displayImages(result.items);
        updateImagesPagination(result);
        
        // 更新当前页码
        imagesCurrentPage = page;
        
    } catch (error) {
        console.error('加载影像失败:', error);
        showEmptyTable(tbody, '加载失败，请重试', 8);
    }
}

// 显示影像数据
function displayImages(items) {
    const tbody = document.getElementById('images-table-body');
    if (!tbody) return;

    const fragment = document.createDocumentFragment();
    items.forEach(item => {
        const tr = document.createElement('tr');
        tr.setAttribute('onclick', 'toggleSeriesInfo(this)');
        tr.setAttribute('data-study-uid', item.studyInstanceUid);
        tr.innerHTML = `
            <td>${item.patientId || ''}</td>
            <td>${item.patientName || ''}</td>
            <td>${item.accessionNumber || ''}</td>
            <td>${item.modality || ''}</td>
            <td>${formatDate(item.studyDate) || ''}</td>
            <td>${item.studyDescription || ''}</td>
            <td>${item.numberOfInstances || 0}</td>
            <td>
                <button class="btn btn-sm btn-danger" onclick="deleteStudy('${item.studyInstanceUid}', event)" title="删除">
                    <i class="bi bi-trash me-1"></i>删除
                </button>
            </td>
        `;
        fragment.appendChild(tr);
    });

    tbody.innerHTML = '';
    tbody.appendChild(fragment);
}

// 更新影像分页信息
function updateImagesPagination(result) {
    try {
        const { totalCount, page, pageSize, totalPages } = result;
        const start = (page - 1) * pageSize + 1;
        const end = Math.min(page * pageSize, totalCount);
        
        const elements = {
            currentPage: document.getElementById('images-currentPage'),
            currentRange: document.getElementById('images-currentRange'),
            totalCount: document.getElementById('images-totalCount'),
            prevPage: document.getElementById('images-prevPage'),
            nextPage: document.getElementById('images-nextPage')
        };

        // 检查所有必需的元素是否存在
        Object.entries(elements).forEach(([key, element]) => {
            if (!element) throw new Error(`找不到元素: ${key}`);
        });
        
        elements.currentPage.textContent = page;
        elements.currentRange.textContent = totalCount > 0 ? `${start}-${end}` : '0-0';
        elements.totalCount.textContent = totalCount;
        
        elements.prevPage.disabled = page <= 1;
        elements.nextPage.disabled = page >= totalPages || totalCount === 0;
    } catch (error) {
        handleError(error, '更新分页信息失败');
    }
}

// 绑定影像管理相关事件
function bindImagesEvents() {
    try {
        // 分页事件
        const prevPageEl = document.getElementById('images-prevPage');
        const nextPageEl = document.getElementById('images-nextPage');

        if (prevPageEl) {
            prevPageEl.onclick = () => {
                if (imagesCurrentPage > 1) {
                    imagesCurrentPage--;
                    loadImages(imagesCurrentPage);
                }
            };
        }

        if (nextPageEl) {
            nextPageEl.onclick = () => {
                imagesCurrentPage++;
                loadImages(imagesCurrentPage);
            };
        }

        // 搜索表单事件
        const searchForm = document.getElementById('imagesSearchForm');
        if (searchForm) {
            // 搜索提交事件
            searchForm.onsubmit = (e) => {
                e.preventDefault();
                imagesCurrentPage = 1;
                loadImages(1);
            };

            // 重置按钮事件
            const resetButton = searchForm.querySelector('button[type="reset"]');
            if (resetButton) {
                resetButton.onclick = (e) => {
                    e.preventDefault();
                    try {
                        // 先重置表单
                        searchForm.reset();
                        
                        // 再手动清空所有搜索条件（以确保select等特殊控件也被重置）
                        const inputs = {
                            'images-searchPatientId': '',
                            'images-searchPatientName': '',
                            'images-searchAccessionNumber': '',
                            'images-searchStudyDate': '',
                            'images-searchModality': ''
                        };
                        
                        Object.entries(inputs).forEach(([id, value]) => {
                            const element = document.getElementById(id);
                            if (element) {
                                element.value = value;
                            }
                        });
                        
                        // 重置页码并重新加载数据
                        imagesCurrentPage = 1;
                        loadImages(1);
                    } catch (error) {
                        console.error('重置表单失败:', error);
                        window.showToast('重置失败', 'error');
                    }
                };
            }
        }
    } catch (error) {
        console.error('绑定影像管理事件失败:', error);
        window.showToast('初始化失败', 'error');
    }
}

// 删除影像研究
async function deleteStudy(studyInstanceUid, event) {
    if (event) {
        event.stopPropagation();
    }

    if (!await showConfirmDialog('确认删除', '确定要删除这个检查吗？此操作不可恢复。')) {
        return;
    }

    try {
        const response = await fetch(`/api/images/${studyInstanceUid}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error('删除失败');
        }

        window.showToast('操作成功', 'success');
        loadImages(imagesCurrentPage);
    } catch (error) {
        console.error('删除失败:', error);
        window.showToast(error.message || '删除失败', 'error');
    }
}

// 切换序列信息显示
async function toggleSeriesInfo(row) {
    const studyUid = $(row).data('study-uid');
    const seriesRow = $(row).next('.series-info');
    
    if (seriesRow.is(':visible')) {
        seriesRow.hide();
        return;
    }

    try {
        // 显示加载动画
        const loadingRow = $(`
            <tr class="series-info">
                <td colspan="8" class="text-center py-3">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">加载中...</span>
                    </div>
                </td>
            </tr>
        `);
        $(row).after(loadingRow);

        const response = await fetch(`/api/images/${studyUid}/series`);
        if (!response.ok) {
            throw new Error('获取序列数据失败');
        }
        
        const data = await response.json();
        
        // 创建序列信息行
        const seriesInfoRow = $(`
            <tr class="series-info">
                <td colspan="8">
                    <div class="series-container">
                        <table class="table table-sm table-bordered series-detail-table">
                            <thead>
                                <tr>
                                    <th style="width: 50px">序列号</th>
                                    <th style="width: 100px">检查类型</th>
                                    <th style="width: 500px">序列描述</th>
                                    <th style="width: 80px">图像数量</th>
                                    <th style="width: 80px">操作</th>
                                </tr>
                            </thead>
                            <tbody></tbody>
                        </table>
                    </div>
                </td>
            </tr>
        `);
        
        const tbody = seriesInfoRow.find('tbody');
        if (data.length === 0) {
            tbody.append(`
                <tr>
                    <td colspan="5" class="text-center text-muted py-3">
                        <i class="bi bi-inbox fs-2 mb-2 d-block"></i>
                        暂无序列数据
                    </td>
                </tr>
            `);
        } else {
            data.forEach(series => {
                tbody.append(`
                    <tr>
                        <td>${series.seriesNumber || ''}</td>
                        <td>${series.modality || '未知'}</td>
                        <td title="${series.seriesDescription || ''}">${series.seriesDescription || ''}</td>
                        <td>${series.numberOfInstances || 0}</td>
                        <td>
                            <button class="btn btn-sm btn-primary" onclick="previewSeries('${studyUid}', '${series.seriesInstanceUid}')">
                                <i class="bi bi-eye me-1"></i>预览
                            </button>
                        </td>
                    </tr>
                `);
            });
        }
        
        // 移除加载动画和已存在的序列信息行
        $(row).siblings('.series-info').remove();
        // 添加新的序列信息行
        $(row).after(seriesInfoRow);

    } catch (error) {
        console.error('获取序列数据失败:', error);
        window.showToast('获取失败', 'error');
        // 移除加载动画
        $(row).siblings('.series-info').remove();
    }
}

// 预览序列
function previewSeries(studyUid, seriesUid) {
    try {
        return showDialog({
            title: 'DICOM 查看器',
            content: `
                <div style="height: calc(90vh - 120px);">
                    <iframe 
                        src="/viewer.html?studyUid=${encodeURIComponent(studyUid)}&seriesUid=${encodeURIComponent(seriesUid)}"
                        style="width: 100%; height: 100%; border: none;"
                        onload="this.style.opacity='1'"
                    ></iframe>
                </div>
            `,
            showFooter: false,  // 不显示底部按钮
            size: 'xl',  // 使用超大对话框
            fullHeight: true  // 使用全高度
        });
    } catch (error) {
        console.error('预览序列失败:', error);
        window.showToast('预览序列失败', 'error');
    }
}

// 格式化日期
function formatDate(dateStr) {
    if (!dateStr) return '';
    return dateStr.replace(/(\d{4})(\d{2})(\d{2})/, '$1-$2-$3');
} 