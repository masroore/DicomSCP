// 全局变量
let currentWorklistId = null;
const pageSize = 10;
let currentPage = 1;

// 修改 DOMContentLoaded 事件监听
document.addEventListener('DOMContentLoaded', () => {
    // 初始化日期选择器默认值为今天
    const dateInput = document.getElementById('worklist-searchScheduledDate');
    if (dateInput) {
        dateInput.value = new Date().toISOString().slice(0, 10);
    }

    // 绑定事件
    bindWorklistEvents();

    // 加载第一页数据
    loadWorklistData();
});

// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    window.showToast(error.response?.data || error.message, 'error');
}

// 成功提示
function showSuccessMessage(message) {
    window.showToast(message, 'success');
}

// 表单验证
function validateWorklistForm(form) {
    if (!form) {
        form = document.getElementById('worklistForm');
    }

    if (!form.checkValidity()) {
        form.reportValidity();
        return false;
    }

    // 验证年龄
    const ageInput = form.querySelector('#patientAge');
    const age = parseInt(ageInput.value);
    if (!ageInput.value || isNaN(age) || age < 0 || age > 150) {
        window.showToast('请输入有效的年龄（0-150岁）', 'error');
        ageInput.focus();
        return false;
    }

    return true;
}

// 初始化预约模块
function initializeWorklist() {
    try {
        // 绑定Worklist相关事件
        bindWorklistEvents();
        
        // 加载初始数据
        loadWorklistData();
    } catch (error) {
        console.error('初始化预约模块失败:', error);
        window.showToast('初始化失败', 'error');
    }
}

// 绑定Worklist相关事件
function bindWorklistEvents() {
    try {
        // 移除预约按钮事件绑定代码，使用 HTML 中的 onclick 属性
        
        // 分页按钮事件绑定
        const prevPageBtn = document.getElementById('worklist-prevPage');
        const nextPageBtn = document.getElementById('worklist-nextPage');

        if (prevPageBtn) {
            // 移除可能存在的旧事件监听器
            prevPageBtn.replaceWith(prevPageBtn.cloneNode(true));
            const newPrevBtn = document.getElementById('worklist-prevPage');
            newPrevBtn.addEventListener('click', () => {
                console.log('点击上一页，当前页码：', currentPage);
                if (currentPage > 1) {
                    currentPage--;
                    loadWorklistData();
                }
            });
        }

        if (nextPageBtn) {
            // 移除可能存在的旧事件监听器
            nextPageBtn.replaceWith(nextPageBtn.cloneNode(true));
            const newNextBtn = document.getElementById('worklist-nextPage');
            newNextBtn.addEventListener('click', () => {
                const totalPages = parseInt(newNextBtn.getAttribute('data-total-pages') || '1');
                console.log('点击下一页，当前页码：', currentPage, '总页数：', totalPages); // 调试日志
                if (currentPage < totalPages) {
                    currentPage++;
                    loadWorklistData();
                }
            });
        }

        // 搜索表单事件
        const searchForm = document.getElementById('worklistSearchForm');
        if (searchForm) {
            searchForm.addEventListener('submit', (e) => {
                e.preventDefault();
                currentPage = 1;
                loadWorklistData();
            });

            // 重置按钮事件
            const resetButton = searchForm.querySelector('button[type="reset"]');
            if (resetButton) {
                resetButton.addEventListener('click', (e) => {
                    e.preventDefault();
                    searchForm.reset();
                    currentPage = 1;
                    loadWorklistData();
                });
            }
        }
    } catch (error) {
        handleError(error, '绑定事件失败');
    }
}

// 加载 Worklist 数据
async function loadWorklistData() {
    const tbody = document.getElementById('worklist-table-body');
    if (!tbody) return;

    showTableLoading(tbody, 8);

    try {
        // 获取搜索条件
        const patientId = document.getElementById('worklist-searchPatientId')?.value || '';
        const patientName = document.getElementById('worklist-searchPatientName')?.value || '';
        const accessionNumber = document.getElementById('worklist-searchAccessionNumber')?.value || '';
        const modality = document.getElementById('worklist-searchModality')?.value || '';
        const scheduledDate = document.getElementById('worklist-searchScheduledDate')?.value || '';

        // 构建查询参数
        const params = new URLSearchParams({
            page: currentPage,
            pageSize: pageSize
        });
        
        if (patientId) params.append('patientId', patientId);
        if (patientName) params.append('patientName', patientName);
        if (accessionNumber) params.append('accessionNumber', accessionNumber);
        if (modality) params.append('modality', modality);
        if (scheduledDate) params.append('scheduledDate', scheduledDate);

        const response = await fetch(`/api/worklist?${params}`);
        if (!response.ok) {
            throw new Error('获取数据失败');
        }
        
        const result = await response.json();
        if (!result) return;

        // 确保返回的数据包含所需的分页信息
        const { items, totalCount, page, totalPages } = result;

        if (!items || items.length === 0) {
            showEmptyTable(tbody, '暂无预约检查', 8);
            // 重置分页信息
            updatePagination(0, 1, 1);
            return;
        }

        // 更新表格数据
        displayWorklistData(items);
        
        // 更新分页信息
        updatePagination(totalCount, page, totalPages);
        
        // 更新当前页码
        currentPage = page;
    } catch (error) {
        handleError(error, '获取预约数据失败');
        showEmptyTable(tbody, '加载失败，请重试', 8);
    }
}

// 显示数据
function displayWorklistData(items) {
    try {
        const tbody = document.getElementById('worklist-table-body');
        if (!tbody) throw new Error('找不到表格主体');

        const fragment = document.createDocumentFragment();
        items.forEach(item => {
            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td title="${item.patientId}">${item.patientId}</td>
                <td title="${item.patientName}">${item.patientName}</td>
                <td>${formatGender(item.patientSex)}</td>
                <td>${item.age ? item.age + '岁' : ''}</td>
                <td title="${item.accessionNumber}">${item.accessionNumber}</td>
                <td>${item.modality}</td>
                <td>${formatDateTime(item.scheduledDateTime)}</td>
                <td><span class="status-${item.status.toLowerCase()}">${formatStatus(item.status)}</span></td>
                <td>
                    <button type="button" class="btn btn-sm btn-primary edit-btn" data-id="${item.worklistId}">
                        <i class="bi bi-pencil me-1"></i>编辑
                    </button>
                    <button type="button" class="btn btn-sm btn-danger delete-btn" data-id="${item.worklistId}">
                        <i class="bi bi-trash me-1"></i>删除
                    </button>
                </td>
            `;

            // 绑定按钮事件
            const editBtn = tr.querySelector('.edit-btn');
            const deleteBtn = tr.querySelector('.delete-btn');

            if (editBtn) {
                editBtn.addEventListener('click', (e) => {
                    e.preventDefault();
                    const id = editBtn.getAttribute('data-id');
                    if (id) editWorklist(id);
                });
            }

            if (deleteBtn) {
                deleteBtn.addEventListener('click', (e) => {
                    e.preventDefault();
                    const id = deleteBtn.getAttribute('data-id');
                    if (id) deleteWorklist(id);
                });
            }

            fragment.appendChild(tr);
        });

        tbody.innerHTML = '';
        tbody.appendChild(fragment);
    } catch (error) {
        handleError(error, '显示数据失败');
    }
}

// 更新分页信息
function updatePagination(totalCount, currentPage, totalPages) {
    try {
        const currentPageEl = document.getElementById('worklist-currentPage');
        const currentRangeEl = document.getElementById('worklist-currentRange');
        const totalCountEl = document.getElementById('worklist-totalCount');
        const prevPageBtn = document.getElementById('worklist-prevPage');
        const nextPageBtn = document.getElementById('worklist-nextPage');

        if (currentPageEl) currentPageEl.textContent = currentPage;
        
        const start = (currentPage - 1) * pageSize + 1;
        const end = Math.min(currentPage * pageSize, totalCount);
        if (currentRangeEl) {
            currentRangeEl.textContent = totalCount > 0 ? `${start}-${end}` : '0-0';
        }
        if (totalCountEl) totalCountEl.textContent = totalCount;
        
        // 更新按钮状态和数据属性
        if (prevPageBtn) {
            prevPageBtn.disabled = currentPage <= 1;
        }
        if (nextPageBtn) {
            nextPageBtn.disabled = currentPage >= totalPages;
            nextPageBtn.setAttribute('data-total-pages', totalPages);
        }
    } catch (error) {
        handleError(error, '更新分页信息失败');
    }
}

// 打开添加预约模态框
function openAddWorklistModal() {
    try {
        // 获取当前时间并生成默认值
        const now = new Date();
        const year = now.getFullYear();
        const month = String(now.getMonth() + 1).padStart(2, '0');
        const day = String(now.getDate()).padStart(2, '0');
        const hours = String(now.getHours()).padStart(2, '0');
        const minutes = String(now.getMinutes()).padStart(2, '0');
        const seconds = String(now.getSeconds()).padStart(2, '0');
        
        const defaultValues = {
            patientId: `P${year}${month}${day}${hours}${minutes}${seconds}`,
            accessionNumber: `A${year}${month}${day}${hours}${minutes}${seconds}`,
            scheduledDateTime: `${year}-${month}-${day}T${hours}:${minutes}`,
            status: 'SCHEDULED'  // 添加默认状态
        };

        return showDialog({
            title: '添加预约',
            content: document.getElementById('worklistForm').outerHTML,
            size: 'lg',
            onShow: () => {
                // 使用 setTimeout 确保 DOM 已更新
                setTimeout(() => {
                    const form = document.querySelector('.modal.show form');
                    if (form) {
                        // 重置表单
                        form.reset();

                        // 填充默认值
                        const fields = {
                            '#patientId': defaultValues.patientId,
                            '#accessionNumber': defaultValues.accessionNumber,
                            '#scheduledDateTime': defaultValues.scheduledDateTime,
                            '#status': defaultValues.status,
                            // 可以在这里添加其他默认值
                            '#modality': 'CT',  // 默认检查类型
                            '#patientSex': 'M'  // 默认性别
                        };

                        // 遍历填充字段
                        Object.entries(fields).forEach(([selector, value]) => {
                            const element = form.querySelector(selector);
                            if (element) {
                                element.value = value;
                            }
                        });
                    } else {
                        console.error('找不到表单元素');
                    }
                }, 100);
            },
            onConfirm: async () => {
                const form = document.querySelector('.modal.show form');
                if (!form) {
                    throw new Error('找不到表单');
                }

                if (!validateWorklistForm(form)) {
                    return false;
                }

                const data = {
                    worklistId: '',  // 新增时为空
                    patientId: form.querySelector('#patientId').value.trim(),
                    patientName: form.querySelector('#patientName').value.trim(),
                    patientSex: form.querySelector('#patientSex').value,
                    age: parseInt(form.querySelector('#patientAge').value),
                    accessionNumber: form.querySelector('#accessionNumber').value.trim(),
                    modality: form.querySelector('#modality').value,
                    scheduledDateTime: form.querySelector('#scheduledDateTime').value,
                    scheduledAET: form.querySelector('#scheduledAET').value.trim(),
                    scheduledStationName: form.querySelector('#scheduledStationName').value.trim(),
                    bodyPartExamined: form.querySelector('#bodyPartExamined').value.trim(),
                    status: form.querySelector('#status').value
                };

                try {
                    const response = await fetch('/api/worklist', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify(data)
                    });

                    if (!response.ok) {
                        const errorText = await response.text();
                        throw new Error(errorText || '保存失败');
                    }

                    window.showToast('预约已添加', 'success');
                    loadWorklistData();
                    return true;
                } catch (error) {
                    handleError(error, '保存预约数据失败');
                    return false;
                }
            }
        });
        
    } catch (error) {
        handleError(error, '打开添加预约窗口失败');
    }
}

// 编辑预约
async function editWorklist(worklistId) {
    try {
        // 先获取数据
        const response = await axios.get(`/api/worklist/${worklistId}`);
        const data = response.data;
        
        // 设置当前编辑的ID
        currentWorklistId = worklistId;

        return showDialog({
            title: '编辑预约',
            content: document.getElementById('worklistForm').outerHTML,
            size: 'lg',
            onShow: () => {
                // 使用 setTimeout 确保 DOM 已更新
                setTimeout(() => {
                    // 填充表单数据
                    const form = document.querySelector('.modal.show form');
                    if (form) {
                        // 填充基本信息
                        const fields = {
                            '#patientId': data.patientId,
                            '#patientName': data.patientName,
                            '#patientSex': data.patientSex,
                            '#patientAge': data.age,
                            '#accessionNumber': data.accessionNumber,
                            '#modality': data.modality,
                            '#scheduledAET': data.scheduledAET,
                            '#scheduledStationName': data.scheduledStationName,
                            '#bodyPartExamined': data.bodyPartExamined,
                            '#status': data.status || 'SCHEDULED'
                        };

                        // 遍历填充字段
                        Object.entries(fields).forEach(([selector, value]) => {
                            const element = form.querySelector(selector);
                            if (element) {
                                element.value = value || '';
                            }
                        });

                        // 格式化预约时间
                        if (data.scheduledDateTime) {
                            try {
                                const date = new Date(data.scheduledDateTime);
                                if (!isNaN(date)) {
                                    const dateInput = form.querySelector('#scheduledDateTime');
                                    if (dateInput) {
                                        dateInput.value = date.toISOString().slice(0, 16);
                                    }
                                }
                            } catch (error) {
                                console.warn('日期格式化失败:', error);
                            }
                        }
                    } else {
                        console.error('找不到表单元素');
                    }
                }, 100);  // 给一个小延时确保 DOM 已更新
            },
            onConfirm: async () => {
                const form = document.querySelector('.modal.show form');
                if (!form) {
                    throw new Error('找不到表单');
                }

                if (!validateWorklistForm(form)) {
                    return false;
                }

                const data = {
                    worklistId: currentWorklistId,
                    patientId: form.querySelector('#patientId').value.trim(),
                    patientName: form.querySelector('#patientName').value.trim(),
                    patientSex: form.querySelector('#patientSex').value,
                    age: parseInt(form.querySelector('#patientAge').value),
                    accessionNumber: form.querySelector('#accessionNumber').value.trim(),
                    modality: form.querySelector('#modality').value,
                    scheduledDateTime: form.querySelector('#scheduledDateTime').value,
                    scheduledAET: form.querySelector('#scheduledAET').value.trim(),
                    scheduledStationName: form.querySelector('#scheduledStationName').value.trim(),
                    bodyPartExamined: form.querySelector('#bodyPartExamined').value.trim(),
                    status: form.querySelector('#status').value
                };

                try {
                    const response = await fetch(`/api/worklist/${currentWorklistId}`, {
                        method: 'PUT',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify(data)
                    });

                    if (!response.ok) {
                        const errorText = await response.text();
                        throw new Error(errorText || '保存失败');
                    }

                    window.showToast('预约已更新', 'success');
                    loadWorklistData();
                    return true;
                } catch (error) {
                    handleError(error, '保存预约数据失败');
                    return false;
                }
            }
        });
        
    } catch (error) {
        handleError(error, '获取预约数据失败');
    }
}

// 删除 Worklist
async function deleteWorklist(id) {
    try {
        if (!await showConfirmDialog('确认删除', '确定要删除这条检查记录吗？')) {
            return;
        }

        const response = await fetch(`/api/worklist/${id}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error('删除失败');
        }

        window.showToast('预约已删除', 'success');
        loadWorklistData();
    } catch (error) {
        handleError(error, '删除预约失败');
    }
}

// 工具函数
function formatDateTime(dateTimeStr) {
    try {
        if (!dateTimeStr) {
            return '';
        }
        
        // 处理 YYYYMMDD 格式
        if (dateTimeStr.length === 8 && !isNaN(dateTimeStr)) {
            const year = dateTimeStr.substring(0, 4);
            const month = dateTimeStr.substring(4, 6);
            const day = dateTimeStr.substring(6, 8);
            return `${year}-${month}-${day}`;
        }
        
        // 处理其他格式的日期
        const date = new Date(dateTimeStr);
        if (isNaN(date.getTime())) {
            console.warn('无法解析的日期时间:', dateTimeStr);
            return dateTimeStr;
        }
        
        return date.toLocaleString('zh-CN', {
            year: 'numeric',
            month: '2-digit',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit'
        });
    } catch (error) {
        console.error('格式化日期时间失败:', error);
        return dateTimeStr;
    }
}

function formatStatus(status) {
    const statusMap = {
        'SCHEDULED': '已预约',
        'IN_PROGRESS': '检查中',
        'COMPLETED': '已完成',
        'DISCONTINUED': '已中断'
    };
    return statusMap[status] || status;
}

function formatGender(gender) {
    const genderMap = {
        'M': '男',
        'F': '女',
        'O': '其他'
    };
    return genderMap[gender] || gender;
} 