// 全局变量
let worklistModal;
let currentWorklistId = null;

// 页面加载完成后执行
$(document).ready(function() {
    // 初始化模态框
    const modalElement = document.getElementById('worklistModal');
    if (modalElement) {
        worklistModal = new bootstrap.Modal(modalElement);
    }
    
    // 加载 Worklist 数据
    loadWorklistData();

    // 处理导航点击
    $('.nav-link').click(function(e) {
        e.preventDefault();
        $('.nav-link').removeClass('active');
        $(this).addClass('active');
        
        const page = $(this).data('page');
        loadPage(page);
    });

    // 默认激活 Worklist 管理
    $('.nav-link[data-page="worklist"]').addClass('active');
});

// 加载页面内容
function loadPage(page) {
    switch(page) {
        case 'worklist':
            $('#worklist-page').show();
            $('#images-page').hide();
            loadWorklistData();
            break;
        case 'images':
            $('#worklist-page').hide();
            $('#images-page').show();
            loadImagesData();
            break;
        case 'settings':
            // TODO: 加载系统设置页面
            break;
    }
}

// 加载 Worklist 数据
function loadWorklistData() {
    fetch('/api/worklist')
        .then(response => response.json())
        .then(data => {
            const tbody = $('#worklist-table-body');
            tbody.empty();
            
            data.forEach(item => {
                tbody.append(`
                    <tr>
                        <td title="${item.patientId}">${item.patientId}</td>
                        <td title="${item.patientName}">${item.patientName}</td>
                        <td>${formatGender(item.patientSex)}</td>
                        <td>${item.age ? item.age + '岁' : ''}</td>
                        <td title="${item.accessionNumber}">${item.accessionNumber}</td>
                        <td>${item.modality}</td>
                        <td>${formatDateTime(item.scheduledDateTime)}</td>
                        <td><span class="status-${item.status.toLowerCase()}">${formatStatus(item.status)}</span></td>
                        <td>
                            <button class="btn btn-sm btn-primary" onclick="editWorklist('${item.worklistId}')">编辑</button>
                            <button class="btn btn-sm btn-danger" onclick="deleteWorklist('${item.worklistId}')">删除</button>
                        </td>
                    </tr>
                `);
            });
        })
        .catch(error => {
            console.error('获取Worklist数据失败:', error);
            alert('获取数据失败，请检查网络连接');
        });
}

// 加载影像数据
function loadImagesData() {
    fetch('/api/images')
        .then(response => response.json())
        .then(data => {
            const tbody = $('#images-table-body');
            tbody.empty();
            
            data.forEach(item => {
                tbody.append(`
                    <tr data-study-uid="${item.studyInstanceUid}" onclick="toggleSeriesInfo(this)">
                        <td title="${item.patientId}">${item.patientId}</td>
                        <td title="${item.patientName}">${item.patientName}</td>
                        <td>${formatGender(item.patientSex)}</td>
                        <td>${calculatePatientAge(item.patientBirthDate)}</td>
                        <td title="${item.accessionNumber}">${item.accessionNumber}</td>
                        <td>${item.modality}</td>
                        <td>${formatDateTime(item.studyDate)}</td>
                        <td title="${item.studyDescription || ''}">${item.studyDescription || ''}</td>
                    </tr>
                    <tr class="series-info" style="display: none;">
                        <td colspan="8">
                            <div class="series-container">
                                <table class="table table-sm">
                                    <thead>
                                        <tr>
                                            <th style="width: 80px">序列号</th>
                                            <th style="width: 80px">检查类型</th>
                                            <th>序列描述</th>
                                            <th style="width: 80px">图像数量</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                    </tbody>
                                </table>
                            </div>
                        </td>
                    </tr>
                `);
            });
        })
        .catch(error => {
            console.error('获取影像数据失败:', error);
            alert('获取数据失败，请检查网络连接');
        });
}

// 切换序列信息显示
function toggleSeriesInfo(row) {
    const studyUid = $(row).data('study-uid');
    const seriesRow = $(row).next('.series-info');
    
    if (seriesRow.is(':visible')) {
        seriesRow.hide();
        return;
    }

    // 加载序列信息
    fetch(`/api/images/${studyUid}/series`)
        .then(response => response.json())
        .then(data => {
            const tbody = seriesRow.find('tbody');
            tbody.empty();
            
            data.forEach(series => {
                tbody.append(`
                    <tr>
                        <td>${series.seriesNumber}</td>
                        <td>${series.modality || '未知'}</td>
                        <td title="${series.seriesDescription || ''}">${series.seriesDescription || ''}</td>
                        <td>${series.numberOfInstances} 张</td>
                    </tr>
                `);
            });
            
            seriesRow.show();
        })
        .catch(error => {
            console.error('获取序列数据失败:', error);
            alert('获取序列数据失败');
        });
}

// 显示添加 Worklist 模态框
function showAddWorklistModal() {
    currentWorklistId = null;
    document.getElementById('worklistForm').reset();
    
    // 设置预约时间默认为当前时间
    const now = new Date();
    const defaultDateTime = now.toISOString().slice(0, 16);
    document.getElementById('scheduledDateTime').value = defaultDateTime;
    document.getElementById('patientAge').value = '';
    document.getElementById('modalTitle').textContent = '添加检查';
    worklistModal.show();
}

// 编辑 Worklist
function editWorklist(id) {
    currentWorklistId = id;
    
    fetch(`/api/worklist/${id}`)
        .then(response => response.json())
        .then(data => {
            document.getElementById('patientId').value = data.patientId || '';
            document.getElementById('patientName').value = data.patientName || '';
            document.getElementById('patientSex').value = data.patientSex || '';
            document.getElementById('patientAge').value = data.age || '';
            document.getElementById('accessionNumber').value = data.accessionNumber || '';
            document.getElementById('modality').value = data.modality || '';
            document.getElementById('scheduledDateTime').value = formatDateTimeForInput(data.scheduledDateTime);
            document.getElementById('scheduledAET').value = data.scheduledAET || '';
            document.getElementById('scheduledStationName').value = data.scheduledStationName || '';
            document.getElementById('bodyPartExamined').value = data.bodyPartExamined || '';
            
            document.getElementById('modalTitle').textContent = '编辑检查';
            worklistModal.show();
        })
        .catch(error => {
            console.error('获取检查数据失败:', error);
            alert('获取数据失败，请重试');
        });
}

// 保存 Worklist
function saveWorklist() {
    // 验证年龄
    const ageInput = document.getElementById('patientAge');
    if (!ageInput.value) {
        alert('请输入年龄');
        ageInput.focus();
        return;
    }
    
    const age = parseInt(ageInput.value);
    if (isNaN(age) || age < 0 || age > 150) {
        alert('请输入有效的年龄（0-150岁）');
        ageInput.focus();
        return;
    }

    const data = {
        worklistId: currentWorklistId || '',
        patientId: document.getElementById('patientId').value,
        patientName: document.getElementById('patientName').value,
        patientSex: document.getElementById('patientSex').value,
        age: age,
        accessionNumber: document.getElementById('accessionNumber').value,
        modality: document.getElementById('modality').value,
        scheduledDateTime: document.getElementById('scheduledDateTime').value,
        scheduledAET: document.getElementById('scheduledAET').value,
        scheduledStationName: document.getElementById('scheduledStationName').value,
        bodyPartExamined: document.getElementById('bodyPartExamined').value,
        status: 'SCHEDULED',
        studyDescription: '',
        scheduledProcedureStepID: '',
        scheduledProcedureStepDescription: '',
        requestedProcedureID: '',
        requestedProcedureDescription: '',
        referringPhysicianName: ''
    };

    const url = currentWorklistId ? `/api/worklist/${currentWorklistId}` : '/api/worklist';
    const method = currentWorklistId ? 'PUT' : 'POST';

    fetch(url, {
        method: method,
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(data)
    })
    .then(async response => {
        const text = await response.text();
        if (!response.ok) {
            throw new Error(`保存失败: ${text}`);
        }
        try {
            const data = text ? JSON.parse(text) : {};
            return data;
        } catch (e) {
            console.warn('响应不是 JSON 格式:', text);
            return {};
        }
    })
    .then(data => {
        console.log('保存成功:', data);
        worklistModal.hide();
        loadWorklistData();
    })
    .catch(error => {
        console.error('保存检查数据失败:', error);
        alert(`保存失败: ${error.message}`);
    });
}
// 删除 Worklist
function deleteWorklist(id) {
    if (!confirm('确定要删除这条检查记录吗？')) {
        return;
    }

    fetch(`/api/worklist/${id}`, {
        method: 'DELETE'
    })
    .then(response => {
        if (!response.ok) {
            throw new Error('删除失败');
        }
        loadWorklistData();
    })
    .catch(error => {
        console.error('删除检查数据失败:', error);
        alert('删除失败，请重试');
    });
}

// 格式化日期时间为input标签所需格式
function formatDateTimeForInput(dateTimeStr) {
    try {
        if (!dateTimeStr) {
            return '';
        }
        
        if (!isNaN(dateTimeStr)) {
            const timestamp = parseInt(dateTimeStr);
            dateTimeStr = new Date(timestamp).toISOString();
        }
        
        const date = new Date(dateTimeStr);
        if (isNaN(date.getTime())) {
            console.error('无效的日期时间:', dateTimeStr);
            return '';
        }
        
        return date.toISOString().slice(0, 16);
    } catch (error) {
        console.error('格式化日期时间失败:', error);
        return '';
    }
}

// 格式化日期时间显示
function formatDateTime(dateTimeStr) {
    try {
        if (!dateTimeStr) {
            return '';
        }
        
        const date = new Date(dateTimeStr);
        if (isNaN(date.getTime())) {
            console.error('无效的日期时间:', dateTimeStr);
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

// 格式化状态显示
function formatStatus(status) {
    const statusMap = {
        'SCHEDULED': '已预约',
        'IN_PROGRESS': '检查中',
        'COMPLETED': '已完成',
        'CANCELLED': '已取消'
    };
    return statusMap[status] || status;
}

// 格式化性别显示
function formatGender(gender) {
    const genderMap = {
        'M': '男',
        'F': '女',
        'O': '其他'
    };
    return genderMap[gender] || gender;
}

// 计算患者年龄
function calculatePatientAge(birthDate) {
    if (!birthDate) return '';
    
    try {
        const today = new Date();
        const birthYear = parseInt(birthDate.substring(0, 4));
        const birthMonth = parseInt(birthDate.substring(4, 6)) - 1;
        const birthDay = parseInt(birthDate.substring(6, 8));
        
        const birthDateTime = new Date(birthYear, birthMonth, birthDay);
        let age = today.getFullYear() - birthDateTime.getFullYear();
        
        // 检查是否已过生日
        if (today.getMonth() < birthDateTime.getMonth() || 
            (today.getMonth() === birthDateTime.getMonth() && today.getDate() < birthDateTime.getDate())) {
            age--;
        }
        
        return `${age}岁`;
    } catch (error) {
        console.error('计算年龄失败:', error);
        return '';
    }
}

