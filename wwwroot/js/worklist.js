// 全局变量
let worklistModal;
let currentWorklistId = null;
const pageSize = 10;
let currentPage = 1;

// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    showToast('error', '操作失败', error.response?.data || error.message);
}

// 成功提示
function showSuccessMessage(message) {
    showToast('success', '操作成功', message);
}

// 表单验证
function validateWorklistForm() {
    const form = document.getElementById('worklistForm');
    if (!form.checkValidity()) {
        form.reportValidity();
        return false;
    }

    // 验证年龄
    const ageInput = document.getElementById('patientAge');
    const age = parseInt(ageInput.value);
    if (!ageInput.value || isNaN(age) || age < 0 || age > 150) {
        showToast('error', '验证失败', '请输入有效的年龄（0-150岁）');
        ageInput.focus();
        return false;
    }

    return true;
}

// 初始化预约模块
function initializeWorklist() {
    try {
        // 初始化模态框
        const modalElement = document.getElementById('worklistModal');
        if (modalElement) {
            worklistModal = new bootstrap.Modal(modalElement);
        }
        
        // 绑定Worklist相关事件
        bindWorklistEvents();
        
        // 加载初始数据
        loadWorklistData();
    } catch (error) {
        console.error('初始化预约模块失败:', error);
        showToast('error', '初始化失败', '初始化预约模块失败');
    }
}

// 绑定Worklist相关事件
function bindWorklistEvents() {
    try {
        // 分页按钮
        const prevPageEl = document.getElementById('worklist-prevPage');
        const nextPageEl = document.getElementById('worklist-nextPage');

        if (prevPageEl) {
            prevPageEl.onclick = () => {
                if (currentPage > 1) {
                    currentPage--;
                    loadWorklistData();
                }
            };
        }

        if (nextPageEl) {
            nextPageEl.onclick = () => {
                currentPage++;
                loadWorklistData();
            };
        }

        // 搜索表单事件
        const searchForm = document.getElementById('worklistSearchForm');
        if (searchForm) {
            searchForm.onsubmit = (e) => {
                e.preventDefault();
                currentPage = 1;
                loadWorklistData();
            };

            // 重置按钮事件
            const resetButton = searchForm.querySelector('button[type="reset"]');
            if (resetButton) {
                resetButton.onclick = (e) => {
                    e.preventDefault();
                    searchForm.reset();
                    currentPage = 1;
                    loadWorklistData();
                };
            }
        }
    } catch (error) {
        handleError(error, '绑定事件失败');
    }
}

// 加载 Worklist 数据
async function loadWorklistData() {
    const tbody = document.getElementById('worklist-table-body');
    showTableLoading(tbody, 8);  // 只在这里显示加载动画

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

        if (result.items.length === 0) {
            showEmptyTable(tbody, '暂无预约检查', 8);
            return;
        }

        // 更新表格数据
        displayWorklistData(result.items);
        
        // 更新分页信息
        updatePagination(result.totalCount, result.page, result.totalPages);
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
                    <button class="btn btn-sm btn-primary" onclick="editWorklist('${item.worklistId}')">
                        <i class="bi bi-pencil me-1"></i>编辑
                    </button>
                    <button class="btn btn-sm btn-danger" onclick="deleteWorklist('${item.worklistId}')">
                        <i class="bi bi-trash me-1"></i>删除
                    </button>
                </td>
            `;
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
        const prevPageEl = document.getElementById('worklist-prevPage');
        const nextPageEl = document.getElementById('worklist-nextPage');

        if (currentPageEl) currentPageEl.textContent = currentPage;
        
        const start = (currentPage - 1) * pageSize + 1;
        const end = Math.min(currentPage * pageSize, totalCount);
        if (currentRangeEl) {
            currentRangeEl.textContent = totalCount > 0 ? `${start}-${end}` : '0-0';
        }
        if (totalCountEl) totalCountEl.textContent = totalCount;
        
        // 更新按钮状态
        if (prevPageEl) prevPageEl.disabled = currentPage <= 1;
        if (nextPageEl) nextPageEl.disabled = currentPage >= totalPages;
    } catch (error) {
        handleError(error, '更新分页信息失败');
    }
}

// 打开添加预约模态框
function openAddWorklistModal() {
    try {
        currentWorklistId = null;
        document.getElementById('modalTitle').textContent = '添加预约';
        
        // 重置表单
        document.getElementById('worklistForm').reset();
        
        // 获取当前时间
        const now = new Date();
        const year = now.getFullYear();
        const month = String(now.getMonth() + 1).padStart(2, '0');
        const day = String(now.getDate()).padStart(2, '0');
        const hours = String(now.getHours()).padStart(2, '0');
        const minutes = String(now.getMinutes()).padStart(2, '0');
        const seconds = String(now.getSeconds()).padStart(2, '0');
        
        // 生成患者ID：P + 年月日时分秒
        const patientId = `P${year}${month}${day}${hours}${minutes}${seconds}`;
        document.getElementById('patientId').value = patientId;
        
        // 生成检查号：A + 年月日时分秒
        const accessionNumber = `A${year}${month}${day}${hours}${minutes}${seconds}`;
        document.getElementById('accessionNumber').value = accessionNumber;
        
        // 设置默认预约时间为当前时间
        const defaultDateTime = `${year}-${month}-${day}T${hours}:${minutes}`;
        document.getElementById('scheduledDateTime').value = defaultDateTime;
        
        // 显示模态框
        worklistModal.show();
    } catch (error) {
        handleError(error, '打开添加预约窗口失败');
    }
}

// 编辑预约
async function editWorklist(worklistId) {
    try {
        currentWorklistId = worklistId;
        document.getElementById('modalTitle').textContent = '编辑预约';
        
        const response = await axios.get(`/api/worklist/${worklistId}`);
        const data = response.data;

        // 填充表单数据
        document.getElementById('patientId').value = data.patientId || '';
        document.getElementById('patientName').value = data.patientName || '';
        document.getElementById('patientSex').value = data.patientSex || '';
        document.getElementById('patientAge').value = data.age || '';
        document.getElementById('accessionNumber').value = data.accessionNumber || '';
        document.getElementById('modality').value = data.modality || '';
        document.getElementById('scheduledAET').value = data.scheduledAET || '';
        document.getElementById('scheduledStationName').value = data.scheduledStationName || '';
        document.getElementById('bodyPartExamined').value = data.bodyPartExamined || '';
        document.getElementById('status').value = data.status || 'SCHEDULED';
        
        // 格式化预约时间
        if (data.scheduledDateTime) {
            try {
                const date = new Date(data.scheduledDateTime);
                if (!isNaN(date)) {
                    const year = date.getFullYear();
                    const month = String(date.getMonth() + 1).padStart(2, '0');
                    const day = String(date.getDate()).padStart(2, '0');
                    const hours = String(date.getHours()).padStart(2, '0');
                    const minutes = String(date.getMinutes()).padStart(2, '0');
                    
                    document.getElementById('scheduledDateTime').value = 
                        `${year}-${month}-${day}T${hours}:${minutes}`;
                }
            } catch (error) {
                console.warn('日期格式化失败:', error);
            }
        }
        
        worklistModal.show();
    } catch (error) {
        handleError(error, '获取预约数据失败');
    }
}

// 保存 Worklist
async function saveWorklist() {
    try {
        if (!validateWorklistForm()) {
            return;
        }

        const data = {
            worklistId: currentWorklistId || '',
            patientId: document.getElementById('patientId').value,
            patientName: document.getElementById('patientName').value,
            patientSex: document.getElementById('patientSex').value,
            age: parseInt(document.getElementById('patientAge').value),
            accessionNumber: document.getElementById('accessionNumber').value,
            modality: document.getElementById('modality').value,
            scheduledDateTime: document.getElementById('scheduledDateTime').value,
            scheduledAET: document.getElementById('scheduledAET').value,
            scheduledStationName: document.getElementById('scheduledStationName').value,
            bodyPartExamined: document.getElementById('bodyPartExamined').value,
            status: document.getElementById('status').value,
            studyDescription: '',
            scheduledProcedureStepID: '',
            scheduledProcedureStepDescription: '',
            requestedProcedureID: '',
            requestedProcedureDescription: '',
            referringPhysicianName: ''
        };

        const url = currentWorklistId ? `/api/worklist/${currentWorklistId}` : '/api/worklist';
        const method = currentWorklistId ? 'PUT' : 'POST';

        const response = await fetch(url, {
            method: method,
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(data)
        });

        if (!response.ok) {
            const errorText = await response.text();
            throw new Error(errorText || '保存失败');
        }

        showSuccessMessage(currentWorklistId ? '预约更新成功' : '预约添加成功');
        worklistModal.hide();
        loadWorklistData();
    } catch (error) {
        handleError(error, '保存预约数据失败');
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

        showSuccessMessage('预约删除成功');
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