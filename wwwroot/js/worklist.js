// 全局变量
let currentWorklistId = null;
const pageSize = 10;
let currentPage = 1;
let isLoading = false;

// 修改 DOMContentLoaded 事件监听
document.addEventListener('DOMContentLoaded', () => {
    // 绑定事件
    bindWorklistEvents();

    // 加载第一页数据
    loadWorklistData();
});

// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    let errorMessage = message;
    
    if (error.response) {
        // axios 错误响应
        errorMessage = error.response.data || error.message || message;
    } else if (error.message) {
        errorMessage = error.message;
    }
    
    window.showToast(errorMessage, 'error');
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

    // 验证必填字段
    const requiredFields = ['patientId', 'patientName', 'accessionNumber', 'scheduledDateTime', 'modality'];
    for (const fieldId of requiredFields) {
        const field = form.querySelector(`#${fieldId}`);
        if (!field || !field.value.trim()) {
            window.showToast(`${fieldId === 'modality' ? '检查类型' : fieldId} 不能为空`, 'error');
            field?.focus();
            return false;
        }
    }

    // 验证年龄
    const ageInput = form.querySelector('#patientAge');
    const age = parseInt(ageInput.value);
    if (!ageInput.value || isNaN(age) || age < 0 || age > 150) {
        window.showToast('请输入有效的年龄（0-150岁）', 'error');
        ageInput.focus();
        return false;
    }

    // 移除预约时间验证
    // const scheduledDateTime = new Date(form.querySelector('#scheduledDateTime').value);
    // if (scheduledDateTime < new Date()) {
    //     window.showToast('预约时间不能早于当前时间', 'error');
    //     return false;
    // }

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

// 发送到后端时的日期时间格式化
function formatDateTimeForServer(dateTimeStr) {
    try {
        if (!dateTimeStr) return '';
        
        // 如果已经是 ISO 格式，直接返回
        if (dateTimeStr.includes('T')) {
            return dateTimeStr;
        }
        
        // 处理其他格式
        const date = new Date(dateTimeStr);
        if (!isNaN(date.getTime())) {
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            const hour = String(date.getHours()).padStart(2, '0');
            const minute = String(date.getMinutes()).padStart(2, '0');
            
            return `${year}-${month}-${day}T${hour}:${minute}`;
        }
        
        return dateTimeStr;
    } catch (error) {
        console.error('格式化日期时间失败:', error);
        return dateTimeStr;
    }
}

// 加载 Worklist 数据
async function loadWorklistData() {
    if (isLoading) return;
    
    const tbody = document.getElementById('worklist-table-body');
    if (!tbody) return;

    isLoading = true;
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
        
        if (scheduledDate) {
            // 添加调试日志
            console.log('查询日期 - 原始值:', scheduledDate);
            
            // 确保使用带 T 的 ISO 格式
            const date = new Date(scheduledDate);
            if (!isNaN(date.getTime())) {
                const formattedDate = formatDateTimeForServer(scheduledDate);
                params.append('scheduledDate', formattedDate);
                console.log('查询日期 - 格式化后:', formattedDate);
            }
        }

        // 添加调试日志
        console.log('查询参数:', Object.fromEntries(params.entries()));

        const response = await axios.get(`/api/worklist`, { params });
        const result = response.data;
        
        if (!result) return;

        // 验证返回数据格式
        if (!Array.isArray(result.items)) {
            throw new Error('返回数据格式错误');
        }

        // 解构后端返回的分页信息
        const { items, totalCount, page, totalPages } = result;
        
        if (typeof totalCount !== 'number' || typeof page !== 'number' || typeof totalPages !== 'number') {
            throw new Error('分页信息格式错误');
        }

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
    } finally {
        isLoading = false;
    }
}

// 显示数据
function displayWorklistData(items) {
    try {
        const tbody = document.getElementById('worklist-table-body');
        if (!tbody) throw new Error('找不到表格主体');

        const fragment = document.createDocumentFragment();
        items.forEach(item => {
            // 添加数据安全处理
            const safeItem = {
                patientId: item.patientId || '',
                patientName: item.patientName || '',
                patientSex: item.patientSex || '',
                age: item.age || '',
                accessionNumber: item.accessionNumber || '',
                modality: item.modality || '',
                scheduledDateTime: item.scheduledDateTime || '',
                status: item.status || 'SCHEDULED',
                worklistId: item.worklistId || ''
            };

            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td title="${safeItem.patientId}">${safeItem.patientId}</td>
                <td title="${safeItem.patientName}">${safeItem.patientName}</td>
                <td>${formatGender(safeItem.patientSex)}</td>
                <td>${safeItem.age ? safeItem.age + '岁' : ''}</td>
                <td title="${safeItem.accessionNumber}">${safeItem.accessionNumber}</td>
                <td>${safeItem.modality}</td>
                <td>${formatDisplayDateTime(safeItem.scheduledDateTime)}</td>
                <td><span class="status-${safeItem.status.toLowerCase()}">${formatStatus(safeItem.status)}</span></td>
                <td>
                    <button type="button" class="btn btn-sm btn-primary edit-btn" data-id="${safeItem.worklistId}">
                        <i class="bi bi-pencil me-1"></i>编辑
                    </button>
                    <button type="button" class="btn btn-sm btn-danger delete-btn" data-id="${safeItem.worklistId}">
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

        // 更新当前页码
        if (currentPageEl) currentPageEl.textContent = currentPage;
        
        // 更新显示范围（如：1-10 / 100）
        const start = (currentPage - 1) * pageSize + 1;
        const end = Math.min(currentPage * pageSize, totalCount);
        if (currentRangeEl) {
            currentRangeEl.textContent = totalCount > 0 ? `${start}-${end}` : '0-0';
        }
        
        // 更新总记录数
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
        return showDialog({
            title: '添加预约',
            content: document.getElementById('worklistForm').outerHTML,
            size: 'lg',
            onShow: () => {
                setTimeout(() => {
                    const form = document.querySelector('.modal.show form');
                    if (form) {
                        // 重置表单
                        form.reset();

                        // 获取当前时间并格式化
                        const now = new Date();
                        const year = now.getFullYear();
                        const month = String(now.getMonth() + 1).padStart(2, '0');
                        const day = String(now.getDate()).padStart(2, '0');
                        const hours = String(now.getHours()).padStart(2, '0');
                        const minutes = String(now.getMinutes()).padStart(2, '0');
                        const seconds = String(now.getSeconds()).padStart(2, '0');

                        // 填充默认值
                        const fields = {
                            '#patientId': `P${year}${month}${day}${hours}${minutes}${seconds}`,
                            '#accessionNumber': `A${year}${month}${day}${hours}${minutes}${seconds}`,
                            '#scheduledDateTime': `${year}-${month}-${day}T${hours}:${minutes}`,
                            '#status': 'SCHEDULED',  // 添加默认状态
                            '#modality': 'CT',       // 默认检查类型
                            '#patientSex': 'M'       // 默认性别
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

                try {
                    const data = {
                        worklistId: '',  // 新增时为空
                        patientId: form.querySelector('#patientId').value.trim(),
                        patientName: form.querySelector('#patientName').value.trim(),
                        patientSex: form.querySelector('#patientSex').value,
                        age: parseInt(form.querySelector('#patientAge').value),
                        accessionNumber: form.querySelector('#accessionNumber').value.trim(),
                        modality: form.querySelector('#modality').value,
                        scheduledDateTime: formatDateTimeForServer(form.querySelector('#scheduledDateTime').value),
                        scheduledAET: form.querySelector('#scheduledAET').value.trim(),
                        scheduledStationName: form.querySelector('#scheduledStationName').value.trim(),
                        bodyPartExamined: form.querySelector('#bodyPartExamined').value.trim(),
                        status: form.querySelector('#status').value
                    };

                    const response = await axios.post('/api/worklist', data);
                    
                    // axios 的响应直接包含数据，不需要调用 text()
                    if (response.status === 200 || response.status === 201) {
                        window.showToast('预约已添加', 'success');
                        loadWorklistData();
                        return true;
                    } else {
                        throw new Error(response.data || '保存失败');
                    }
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
                setTimeout(() => {
                    const form = document.querySelector('.modal.show form');
                    if (form) {
                        // 填充基本信息
                        const fields = {
                            '#patientId': data.patientId,
                            '#patientName': data.patientName,
                            '#patientSex': data.patientSex,
                            '#patientAge': data.age || '',  // 确保即使是 null 也显示为字符串
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
                                // 对于数字类型的输入，确保不显示 'null' 或 'undefined'
                                element.value = (value === null || value === undefined) ? '' : value;
                            }
                        });

                        // 处理预约时间
                        if (data.scheduledDateTime) {
                            const dateInput = form.querySelector('#scheduledDateTime');
                            if (dateInput) {
                                dateInput.value = formatDateTime(data.scheduledDateTime);
                            }
                        }
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

                try {
                    const data = {
                        worklistId: currentWorklistId,
                        patientId: form.querySelector('#patientId').value.trim(),
                        patientName: form.querySelector('#patientName').value.trim(),
                        patientSex: form.querySelector('#patientSex').value,
                        age: parseInt(form.querySelector('#patientAge').value),
                        accessionNumber: form.querySelector('#accessionNumber').value.trim(),
                        modality: form.querySelector('#modality').value,
                        scheduledDateTime: formatDateTimeForServer(form.querySelector('#scheduledDateTime').value),
                        scheduledAET: form.querySelector('#scheduledAET').value.trim(),
                        scheduledStationName: form.querySelector('#scheduledStationName').value.trim(),
                        bodyPartExamined: form.querySelector('#bodyPartExamined').value.trim(),
                        status: form.querySelector('#status').value
                    };

                    const response = await axios.put(`/api/worklist/${currentWorklistId}`, data);
                    
                    if (response.status === 200 || response.status === 204) {
                        window.showToast('预约已更新', 'success');
                        loadWorklistData();
                        return true;
                    } else {
                        throw new Error(response.data || '保存失败');
                    }
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
async function deleteWorklist(id, event) {
    if (event) {
        event.stopPropagation();
    }

    if (!await showConfirmDialog('确认删除', '确定要删除这个预约吗？此操作不可恢复。')) {
        return;
    }

    try {
        await axios.delete(`/api/worklist/${id}`);
        window.showToast('删除成功', 'success');

        // 获取当前的数据数量
        const tbody = document.getElementById('worklist-table-body');
        const currentPageItems = tbody.getElementsByTagName('tr').length;
        
        // 如果当前页只有一条数据，且不是第一页，则加载上一页
        if (currentPageItems === 1 && currentPage > 1) {
            currentPage--;
        }
        
        // 重新加载数据
        loadWorklistData();
    } catch (error) {
        handleError(error, '删除失败');
    }
}

// 格式化显示时间（仅用于列表显示）
function formatDisplayDateTime(dateTimeStr) {
    try {
        if (!dateTimeStr) {
            return '';
        }
        
        let date;
        // 处理 DICOM 格式 (yyyyMMddHHmm)
        if (dateTimeStr.length >= 8 && !isNaN(dateTimeStr)) {
            const year = dateTimeStr.substring(0, 4);
            const month = dateTimeStr.substring(4, 6);
            const day = dateTimeStr.substring(6, 8);
            let hour = '00';
            let minute = '00';
            let second = '00';
            
            if (dateTimeStr.length >= 12) {
                hour = dateTimeStr.substring(8, 10);
                minute = dateTimeStr.substring(10, 12);
            }
            
            date = new Date(`${year}-${month}-${day}T${hour}:${minute}:${second}`);
        } else {
            date = new Date(dateTimeStr);
        }
        
        if (!isNaN(date.getTime())) {
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            const hour = String(date.getHours()).padStart(2, '0');
            const minute = String(date.getMinutes()).padStart(2, '0');
            const second = String(date.getSeconds()).padStart(2, '0');
            
            return `${year}/${month}/${day} ${hour}:${minute}:${second}`;
        }
        
        return dateTimeStr;
    } catch (error) {
        console.error('格式化显示日期时间失败:', error);
        return dateTimeStr;
    }
}

// 保持原有的 formatDateTime 函数用于后端交互
function formatDateTime(dateTimeStr) {
    try {
        if (!dateTimeStr) {
            return '';
        }
        
        // 如果已经是 ISO 格式，直接返回
        if (dateTimeStr.includes('T')) {
            return dateTimeStr;
        }
        
        // 处理 DICOM 格式 (yyyyMMddHHmm)
        if (dateTimeStr.length >= 8 && !isNaN(dateTimeStr)) {
            const year = dateTimeStr.substring(0, 4);
            const month = dateTimeStr.substring(4, 6);
            const day = dateTimeStr.substring(6, 8);
            let hour = '00';
            let minute = '00';
            
            if (dateTimeStr.length >= 12) {
                hour = dateTimeStr.substring(8, 10);
                minute = dateTimeStr.substring(10, 12);
            }
            
            return `${year}-${month}-${day}T${hour}:${minute}`;
        }
        
        // 处理其他格式，确保返回 ISO 格式
        const date = new Date(dateTimeStr);
        if (!isNaN(date.getTime())) {
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            const hour = String(date.getHours()).padStart(2, '0');
            const minute = String(date.getMinutes()).padStart(2, '0');
            
            return `${year}-${month}-${day}T${hour}:${minute}`;
        }
        
        return dateTimeStr;
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