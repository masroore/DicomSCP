// 全局变量
let worklistModal;
let currentWorklistId = null;
let changePasswordModal;
let viewerModal;

// 分页参数
let currentPage = 1;
const pageSize = 10;
let allWorklistItems = [];  // 存储所有数据

// 影像数据分页参数
let allImagesData = [];  // 存储所有影像数据
let imagesCurrentPage = 1;
const imagesPageSize = 10;

// QR客户端相关变量
let qrCurrentPage = 1;
const qrPageSize = 10;
let qrAllData = [];
let qrSeriesModal;

// 页面加载完成后执行
$(document).ready(function() {
    // 初始化模态框
    const modalElement = document.getElementById('worklistModal');
    if (modalElement) {
        worklistModal = new bootstrap.Modal(modalElement);
    }
    
    // 初始化修改密码模态框
    const changePasswordModalElement = document.getElementById('changePasswordModal');
    if (changePasswordModalElement) {
        changePasswordModal = new bootstrap.Modal(changePasswordModalElement);
    }
    
    // 初始化查看器模态框
    viewerModal = new bootstrap.Modal(document.getElementById('viewerModal'));
    
    // 监听模态框关闭事件，清理资源
    document.getElementById('viewerModal').addEventListener('hidden.bs.modal', function () {
        document.getElementById('viewerFrame').src = 'about:blank';
    });
    
    // 根据URL hash切换到对应页面，如果没有hash则默认显示worklist
    const currentPage = window.location.hash.slice(1) || 'worklist';
    switchPage(currentPage);
    
    // 导航链接点击事件
    $('.nav-link[data-page]').click(function(e) {
        e.preventDefault();
        const page = $(this).data('page');
        window.location.hash = page; // 更新URL hash
        switchPage(page);
    });

    // 加载初始数据
    loadWorklistData();
    loadImagesData();
    
    // 获取当前登录用户名
    getCurrentUsername();
});

// 切换页面函数
function switchPage(page) {
    // 隐藏所有页面
    $('#worklist-page, #images-page, #settings-page, #qr-page, #store-page').hide();
    
    // 移除所有导航链接的active类
    $('.nav-link').removeClass('active');
    
    // 显示选中的页面
    $(`#${page}-page`).show();
    
    // 添加active类到当前导航链接
    $(`.nav-link[data-page="${page}"]`).addClass('active');
    
    // 根据页面类型加载数据
    if (page === 'worklist') {
        loadWorklistData();
    } else if (page === 'images') {
        loadImagesData();
    } else if (page === 'qr') {
        loadQRNodes();
    } else if (page === 'store') {
        // 如果store.js中的loadStoreNodes函数已经定义，就调用它
        if (typeof loadStoreNodes === 'function') {
            loadStoreNodes();
        }
    }
}

// 加载页面内容
function loadPage(page) {
    switch(page) {
        case 'worklist':
            $('#worklist-page').show();
            $('#images-page, #qr-page, #store-page, #settings-page').hide();
            loadWorklistData();
            break;
        case 'images':
            $('#images-page').show();
            $('#worklist-page, #qr-page, #store-page, #settings-page').hide();
            loadImagesData();
            break;
        case 'qr':
            $('#qr-page').show();
            $('#worklist-page, #images-page, #store-page, #settings-page').hide();
            loadQRNodes();
            break;
        case 'store':
            $('#store-page').show();
            $('#worklist-page, #images-page, #qr-page, #settings-page').hide();
            if (typeof loadStoreNodes === 'function') {
                loadStoreNodes();
            }
            break;
        case 'settings':
            $('#settings-page').show();
            $('#worklist-page, #images-page, #qr-page, #store-page').hide();
            break;
    }
}

// 加载 Worklist 数据
async function loadWorklistData() {
    try {
        const response = await fetch('/api/worklist');
        if (response.status === 401) {
            window.location.href = '/login.html';
            return;
        }
        if (!response.ok) {
            throw new Error('获取数据失败');
        }
        
        const data = await response.json();
        if (!data) return;  // 如果是401跳转，data会是undefined

        // 保存所有数据
        allWorklistItems = data;
        
        // 更新分页信息
        const total = allWorklistItems.length;
        const totalPages = Math.ceil(total / pageSize);
        updatePagination(total);
        
        // 显示当前页数据
        displayWorklistPage(currentPage);
    } catch (error) {
        console.error('获取Worklist数据失败:', error);
        alert('获取数据失败，请检查网络连接');
    }
}

// 显示指定页的数据
function displayWorklistPage(page) {
    const start = (page - 1) * pageSize;
    const end = start + pageSize;
    const pageItems = allWorklistItems.slice(start, end);
    
    const tbody = document.getElementById('worklist-table-body');
    tbody.innerHTML = pageItems.map(item => `
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
    `).join('');
}

// 更新分页信息
function updatePagination(total) {
    const totalPages = Math.ceil(total / pageSize);
    const start = (currentPage - 1) * pageSize + 1;
    const end = Math.min(currentPage * pageSize, total);
    
    document.getElementById('worklist-currentRange').textContent = `${start}-${end}`;
    document.getElementById('worklist-totalCount').textContent = total;
    document.getElementById('worklist-currentPage').textContent = currentPage;
    
    // 更新按钮状态
    document.getElementById('worklist-prevPage').disabled = currentPage === 1;
    document.getElementById('worklist-nextPage').disabled = currentPage === totalPages;
}

// 添加分页事件监听
document.getElementById('worklist-prevPage').onclick = () => {
    if (currentPage > 1) {
        currentPage--;
        displayWorklistPage(currentPage);
        updatePagination(allWorklistItems.length);
    }
};

document.getElementById('worklist-nextPage').onclick = () => {
    const totalPages = Math.ceil(allWorklistItems.length / pageSize);
    if (currentPage < totalPages) {
        currentPage++;
        displayWorklistPage(currentPage);
        updatePagination(allWorklistItems.length);
    }
};

// 加载影像数据
async function loadImagesData() {
    try {
        const response = await fetch('/api/images');
        if (response.status === 401) {
            window.location.href = '/login.html';
            return;
        }
        if (!response.ok) {
            throw new Error('获取数据失败');
        }
        
        const data = await response.json();
        if (!data) return;  // 如果是401跳转，data会是undefined
        
        // 保存所有数据
        allImagesData = data;
        
        // 更新分页信息
        const total = allImagesData.length;
        const totalPages = Math.ceil(total / imagesPageSize);
        updateImagesPagination(total);
        
        // 显示当前页数据
        displayImagesPage(imagesCurrentPage);
    } catch (error) {
        console.error('获取影像数据失败:', error);
        alert('获取数据失败，请检查网络连接');
    }
}

// 显示指定页的影像数据
function displayImagesPage(page) {
    const start = (page - 1) * imagesPageSize;
    const end = start + imagesPageSize;
    const pageItems = allImagesData.slice(start, end);
    
    const tbody = document.getElementById('images-table-body');
    tbody.innerHTML = pageItems.map(item => `
        <tr data-study-uid="${item.studyInstanceUid}" onclick="toggleSeriesInfo(this)">
            <td title="${item.patientId}">${item.patientId}</td>
            <td title="${item.patientName}">${item.patientName}</td>
            <td>${formatGender(item.patientSex)}</td>
            <td>${calculatePatientAge(item.patientBirthDate)}</td>
            <td title="${item.accessionNumber}">${item.accessionNumber}</td>
            <td>${item.modality}</td>
            <td>${formatDateTime(item.studyDate)}</td>
            <td title="${item.studyDescription || ''}">${item.studyDescription || ''}</td>
            <td>
                <button class="btn btn-danger btn-sm" onclick="deleteStudy('${item.studyInstanceUid}', event)">删除</button>
            </td>
        </tr>
    `).join('');
}

// 更新影像分页信息
function updateImagesPagination(total) {
    const totalPages = Math.ceil(total / imagesPageSize);
    const start = (imagesCurrentPage - 1) * imagesPageSize + 1;
    const end = Math.min(imagesCurrentPage * imagesPageSize, total);
    
    document.getElementById('images-currentRange').textContent = `${start}-${end}`;
    document.getElementById('images-totalCount').textContent = total;
    document.getElementById('images-currentPage').textContent = imagesCurrentPage;
    
    // 更新按钮状态
    document.getElementById('images-prevPage').disabled = imagesCurrentPage === 1;
    document.getElementById('images-nextPage').disabled = imagesCurrentPage === totalPages;
}

// 添加影像分页事件监听
document.getElementById('images-prevPage').onclick = () => {
    if (imagesCurrentPage > 1) {
        imagesCurrentPage--;
        displayImagesPage(imagesCurrentPage);
        updateImagesPagination(allImagesData.length);
    }
};

document.getElementById('images-nextPage').onclick = () => {
    const totalPages = Math.ceil(allImagesData.length / imagesPageSize);
    if (imagesCurrentPage < totalPages) {
        imagesCurrentPage++;
        displayImagesPage(imagesCurrentPage);
        updateImagesPagination(allImagesData.length);
    }
};

// 切换序列信息显示
function toggleSeriesInfo(row) {
    const studyUid = $(row).data('study-uid');
    const seriesRow = $(row).next('.series-info');
    
    if (seriesRow.is(':visible')) {
        seriesRow.hide();
        return;
    }

    fetch(`/api/images/${studyUid}/series`)
        .then(response => {
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            if (!response.ok) {
                throw new Error('获取数据失败');
            }
            return response.json();
        })
        .then(data => {
            if (!data) return;  // 如果是401跳转，data会是undefined
            
            // 创建序列信息行
            const seriesInfoRow = $(`
                <tr class="series-info" style="display: none;">
                    <td colspan="9">
                        <div class="series-container">
                            <table class="table table-sm table-bordered series-detail-table">
                                <thead>
                                    <tr>
                                        <th style="width: 50px">序列号</th>
                                        <th style="width: 100px">检查类型</th>
                                        <th style="width: 500px">序列描述</th>
                                        <th style="width: 80px">图像数量</th>
                                        <th style="width: 50px">操作</th>
                                    </tr>
                                </thead>
                                <tbody></tbody>
                            </table>
                        </div>
                    </td>
                </tr>
            `);
            
            const tbody = seriesInfoRow.find('tbody');
            data.forEach(series => {
                tbody.append(`
                    <tr>
                        <td>${series.seriesNumber}</td>
                        <td>${series.modality || '未知'}</td>
                        <td title="${series.seriesDescription || ''}">${series.seriesDescription || ''}</td>
                        <td>${series.numberOfInstances} 张</td>
                        <td>
                            <button class="btn btn-primary btn-sm py-0" onclick="previewSeries('${studyUid}', '${series.seriesInstanceUid}')">
                                预览
                            </button>
                        </td>
                    </tr>
                `);
            });
            
            // 移除已存在的序列信息行
            $(row).siblings('.series-info').remove();
            // 添加新的序列信息行并显示
            $(row).after(seriesInfoRow);
            seriesInfoRow.show();
        })
        .catch(error => {
            console.error('获取序列数据失败:', error);
            alert('获取序列数据失败');
        });
}

// 打开添加预约模态框
function openAddWorklistModal() {
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
}

// 编辑预约时，格式化日期时间
function editWorklist(worklistId) {
    currentWorklistId = worklistId;
    document.getElementById('modalTitle').textContent = '编辑预约';
    
    fetch(`/api/worklist/${worklistId}`)
        .then(response => {
            if (!response.ok) throw new Error('获取数据失败');
            return response.json();
        })
        .then(data => {
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
        })
        .catch(error => {
            console.error('获取预约数据失败:', error);
            alert('获取预约数据失败');
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
        console.error('格式化日期时间败:', error);
        return '';
    }
}

// 格式化日期时间显示
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
        'O': '其'
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

// 显示修改密码模态框
function showChangePasswordModal() {
    document.getElementById('changePasswordForm').reset();
    changePasswordModal.show();
}

// 修改密码
function changePassword() {
    const oldPassword = document.getElementById('oldPassword').value;
    const newPassword = document.getElementById('newPassword').value;
    const confirmPassword = document.getElementById('confirmPassword').value;

    // 验证新密码
    if (newPassword !== confirmPassword) {
        alert('两次输入的新密码不一致');
        return;
    }

    // 验证新密码长度
    if (newPassword.length < 6) {
        alert('新密码长度不能少于6位');
        return;
    }

    fetch('/api/auth/change-password', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            oldPassword: oldPassword,
            newPassword: newPassword
        })
    })
    .then(async response => {
        if (!response.ok) {
            const text = await response.text();
            throw new Error(text);
        }
        // 不尝试解析 JSON，直接处理
        alert('密码修成功，请重登录');
        changePasswordModal.hide();
        window.location.href = '/login.html';
    })
    .catch(error => {
        alert(error.message || '修改密码失败，请重试');
    });
}

// 获取当前用户名
function getCurrentUsername() {
    // 从 cookie 中获取用户名
    const cookies = document.cookie.split(';');
    for(let cookie of cookies) {
        const [name, value] = cookie.trim().split('=');
        if(name === 'username') {
            document.getElementById('currentUsername').textContent = decodeURIComponent(value);
            return;
        }
    }
    // 如果没有找到用户名，可能未登录
    window.location.href = '/login.html';
}

// 删除影像数据
function deleteStudy(studyInstanceUid, event) {
        // 阻止事件冒泡，防止触发行的点击事件
        event.stopPropagation();
        
    if (confirm('确定要删除这个检查吗？\n此操作将删除所有相关的序列和图像，且不可恢复。')) {
        fetch(`/api/images/${studyInstanceUid}`, {
            method: 'DELETE',
            headers: {
                'Content-Type': 'application/json',
            }
        })
        .then(response => {
            if (!response.ok) {
                return response.json().then(data => {
                    throw new Error(data.error || '删除失败');
                });
            }
            // 删除成功后刷新列表
            loadImagesData();
        })
        .catch(error => {
            console.error('删除失败:', error);
            alert(error.message || '删除失败，请重试');
        });
    }
}

// 修改预览函数
function previewSeries(studyUid, seriesUid, event) {
    // 防止事件冒泡
    if (event) {
        event.stopPropagation();
    }
    
    // 设置 iframe 源
    const viewerUrl = `/viewer.html?studyUid=${encodeURIComponent(studyUid)}&seriesUid=${encodeURIComponent(seriesUid)}`;
    document.getElementById('viewerFrame').src = viewerUrl;
    
    // 显示模态框
    viewerModal.show();
}

// 搜索影像数据
function searchImages() {
    const filters = {
        patientId: document.getElementById('searchPatientId').value.trim(),
        patientName: document.getElementById('searchPatientName').value.trim(),
        accessionNumber: document.getElementById('searchAccessionNumber').value.trim(),
        modality: document.getElementById('searchModality').value,
        stationName: document.getElementById('searchStationName').value.trim()
    };

    // 过滤数据
    const filteredData = allImagesData.filter(item => {
        return (!filters.patientId || item.patientId.toLowerCase().includes(filters.patientId.toLowerCase())) &&
               (!filters.patientName || item.patientName.toLowerCase().includes(filters.patientName.toLowerCase())) &&
               (!filters.accessionNumber || item.accessionNumber.toLowerCase().includes(filters.accessionNumber.toLowerCase())) &&
               (!filters.modality || item.modality === filters.modality) &&
               (!filters.stationName || (item.stationName && item.stationName.toLowerCase().includes(filters.stationName.toLowerCase())));
    });

    // 更新分页信息
    const total = filteredData.length;
    const totalPages = Math.ceil(total / imagesPageSize);
    
    // 重置当前页为第一页
    imagesCurrentPage = 1;
    
    // 显示过滤后的数据
    displayFilteredImagesPage(filteredData);
    updateImagesPagination(total);
}

// 重置查询条件
function resetImagesSearch() {
    document.getElementById('imagesSearchForm').reset();
    imagesCurrentPage = 1;
    displayImagesPage(imagesCurrentPage);
    updateImagesPagination(allImagesData.length);
}

// 显示过滤后的数据
function displayFilteredImagesPage(filteredData) {
    const start = (imagesCurrentPage - 1) * imagesPageSize;
    const end = start + imagesPageSize;
    const pageItems = filteredData.slice(start, end);
    
    const tbody = document.getElementById('images-table-body');
    tbody.innerHTML = pageItems.map(item => `
        <tr data-study-uid="${item.studyInstanceUid}" onclick="toggleSeriesInfo(this)">
            <td title="${item.patientId}">${item.patientId}</td>
            <td title="${item.patientName}">${item.patientName}</td>
            <td>${formatGender(item.patientSex)}</td>
            <td>${calculatePatientAge(item.patientBirthDate)}</td>
            <td title="${item.accessionNumber}">${item.accessionNumber}</td>
            <td>${item.modality}</td>
            <td>${formatDateTime(item.studyDate)}</td>
            <td title="${item.studyDescription || ''}">${item.studyDescription || ''}</td>
            <td>
                <button class="btn btn-danger btn-sm" onclick="deleteStudy('${item.studyInstanceUid}', event)">删除</button>
            </td>
        </tr>
    `).join('');
}

// 修改分页事件监听，支持过滤后的数据
document.getElementById('images-prevPage').onclick = () => {
    if (imagesCurrentPage > 1) {
        imagesCurrentPage--;
        const filteredData = getFilteredImagesData();
        displayFilteredImagesPage(filteredData);
        updateImagesPagination(filteredData.length);
    }
};

document.getElementById('images-nextPage').onclick = () => {
    const filteredData = getFilteredImagesData();
    const totalPages = Math.ceil(filteredData.length / imagesPageSize);
    if (imagesCurrentPage < totalPages) {
        imagesCurrentPage++;
        displayFilteredImagesPage(filteredData);
        updateImagesPagination(filteredData.length);
    }
};

// 获取当前过滤后的数据
function getFilteredImagesData() {
    const filters = {
        patientId: document.getElementById('searchPatientId').value.trim(),
        patientName: document.getElementById('searchPatientName').value.trim(),
        accessionNumber: document.getElementById('searchAccessionNumber').value.trim(),
        modality: document.getElementById('searchModality').value,
        stationName: document.getElementById('searchStationName').value.trim()
    };

    // 如果没有设置任何过滤条件，返回所有数据
    if (!Object.values(filters).some(v => v)) {
        return allImagesData;
    }

    // 返回过滤后的数据
    return allImagesData.filter(item => {
        return (!filters.patientId || item.patientId.toLowerCase().includes(filters.patientId.toLowerCase())) &&
               (!filters.patientName || item.patientName.toLowerCase().includes(filters.patientName.toLowerCase())) &&
               (!filters.accessionNumber || item.accessionNumber.toLowerCase().includes(filters.accessionNumber.toLowerCase())) &&
               (!filters.modality || item.modality === filters.modality) &&
               (!filters.stationName || (item.stationName && item.stationName.toLowerCase().includes(filters.stationName.toLowerCase())));
    });
}

// 加载QR节点列表
async function loadQRNodes() {
    try {
        const response = await fetch('/api/QueryRetrieve/nodes');
        if (!response.ok) {
            throw new Error('获取节点列表失败');
        }
        
        const nodes = await response.json();
        const select = document.getElementById('qrNode');
        select.innerHTML = nodes.map(node => `
            <option value="${node.name}">${node.name} (${node.aeTitle}@${node.hostName})</option>
        `).join('');
        
        // 选择默认节点
        const defaultNode = nodes.find(n => n.isDefault);
        if (defaultNode) {
            select.value = defaultNode.name;
        }
    } catch (error) {
        console.error('加载PACS节点失败:', error);
        alert('加载PACS节点失败，请检查网络连接');
    }
}

// 执行QR查询
async function searchQR() {
    const nodeId = document.getElementById('qrNode').value;
    if (!nodeId) {
        alert('请选择PACS节点');
        return;
    }

    const queryParams = {
        patientId: document.getElementById('qrPatientId').value,
        patientName: document.getElementById('qrPatientName').value,
        accessionNumber: document.getElementById('qrAccessionNumber').value,
        modality: document.getElementById('qrModality').value,
        studyDate: document.getElementById('qrStudyDate').value
    };

    try {
        const response = await fetch(`/api/QueryRetrieve/${nodeId}/query/study`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(queryParams)
        });

        if (!response.ok) {
            throw new Error('查询失败');
        }

        const result = await response.json();
        console.log('查询结果:', result);  // 添加调试日志

        // 更新数据和显示
        qrAllData = result;
        qrCurrentPage = 1;
        displayQRPage(qrCurrentPage);
        updateQRPagination(qrAllData.length);
    } catch (error) {
        console.error('查询失败:', error);
        alert('查询失败，请���查网络连接');
    }
}

// 显示QR查询结果页
function displayQRPage(page) {
    const start = (page - 1) * qrPageSize;
    const end = start + qrPageSize;
    const pageItems = qrAllData.slice(start, end);
    
    console.log('显示页面数据:', pageItems);  // 添加调试日志
    
    const tbody = document.getElementById('qr-table-body');
    tbody.innerHTML = pageItems.map(item => `
        <tr data-study-uid="${item.studyInstanceUid}" onclick="toggleQRSeriesInfo(this)">
            <td>${item.patientId || ''}</td>
            <td>${item.patientName || ''}</td>
            <td>${item.accessionNumber || ''}</td>
            <td>${item.modalities || item.modality || ''}</td>
            <td>${formatDate(item.studyDate) || ''}</td>
            <td>${item.studyDescription || ''}</td>
            <td>${item.seriesCount || 0}</td>
            <td>${item.instanceCount || 0}</td>
            <td>
                <button class="btn btn-sm btn-success" onclick="moveQRStudy('${item.studyInstanceUid}', event)">获取</button>
            </td>
        </tr>
    `).join('');
}

// 更新QR分页信息
function updateQRPagination(total) {
    const totalPages = Math.ceil(total / qrPageSize);
    const start = (qrCurrentPage - 1) * qrPageSize + 1;
    const end = Math.min(qrCurrentPage * qrPageSize, total);
    
    document.getElementById('qr-currentRange').textContent = total > 0 ? `${start}-${end}` : '0-0';
    document.getElementById('qr-totalCount').textContent = total;
    document.getElementById('qr-currentPage').textContent = qrCurrentPage;
    
    document.getElementById('qr-prevPage').disabled = qrCurrentPage === 1;
    document.getElementById('qr-nextPage').disabled = qrCurrentPage === totalPages || total === 0;
}

// 切换序列信息显示
async function toggleQRSeriesInfo(row) {
    const studyUid = $(row).data('study-uid');
    const seriesRow = $(row).next('.series-info');
    const nodeId = document.getElementById('qrNode').value;
    
    if (seriesRow.is(':visible')) {
        seriesRow.hide();
        return;
    }

    try {
        const response = await fetch(`/api/QueryRetrieve/${nodeId}/query/series/${studyUid}`, {
            method: 'POST'
        });

        if (!response.ok) {
            throw new Error('获取序列失败');
        }

        const result = await response.json();
        console.log('序列数据:', result);

        // 创建序列信息行
        const seriesInfoRow = $(`
            <tr class="series-info" style="display: none;">
                <td colspan="9">
                    <div class="series-container">
                        <table class="table table-sm table-bordered series-detail-table">
                            <thead>
                                <tr>
                                    <th style="width: 50px">序列号</th>
                                    <th style="width: 100px">检查类型</th>
                                    <th style="width: 500px">序列描述</th>
                                    <th style="width: 80px">图像数量</th>
                                    <th style="width: 50px">操作</th>
                                </tr>
                            </thead>
                            <tbody></tbody>
                        </table>
                    </div>
                </td>
            </tr>
        `);
        
        const tbody = seriesInfoRow.find('tbody');
        result.forEach(series => {
            tbody.append(`
                <tr>
                    <td>${series.seriesNumber || ''}</td>
                    <td>${series.modality || ''}</td>
                    <td title="${series.seriesDescription || ''}">${series.seriesDescription || ''}</td>
                    <td>${series.instanceCount || 0} 张</td>
                    <td>
                        <button class="btn btn-primary btn-sm py-0" onclick="moveQRSeries('${studyUid}', '${series.seriesInstanceUid}', event)">
                            获取
                        </button>
                    </td>
                </tr>
            `);
        });
        
        // 移除已存在的序列信息行
        $(row).siblings('.series-info').remove();
        // 添加新的序列信息行并显示
        $(row).after(seriesInfoRow);
        seriesInfoRow.show();
    } catch (error) {
        console.error('获取序列失败:', error);
        alert('获取序列失败，请检查网络连接');
    }
}

// 获取检查
async function moveQRStudy(studyUid, event) {
    // 阻止事件冒泡，防止触发行的点击事件
    if (event) {
        event.stopPropagation();
    }

    const nodeId = document.getElementById('qrNode').value;
    try {
        const response = await fetch(`/api/QueryRetrieve/${nodeId}/move/${studyUid}`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                destinationAe: 'STORESCP',
                level: 'STUDY'
            })
        });

        if (!response.ok) {
            throw new Error('传输失败');
        }

        const result = await response.json();
        if (result.success) {
            alert('检查传输已开始');
        } else {
            alert(result.message || '传输失败');
        }
    } catch (error) {
        console.error('传输失败:', error);
        alert('传输失败，请检查网络连接');
    }
}

// 获取序列
async function moveQRSeries(studyUid, seriesUid, event) {
    // 阻止事件冒泡
    if (event) {
        event.stopPropagation();
    }

    const nodeId = document.getElementById('qrNode').value;
    try {
        const response = await fetch(`/api/QueryRetrieve/${nodeId}/move/${studyUid}`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                destinationAe: 'STORESCP',
                level: 'SERIES',
                seriesInstanceUid: seriesUid
            })
        });

        if (!response.ok) {
            throw new Error('传输失败');
        }

        const result = await response.json();
        if (result.success) {
            alert('序列传输已开始');
        } else {
            alert(result.message || '传输失败');
        }
    } catch (error) {
        console.error('传输失败:', error);
        alert('传输失败，请检查网络连接');
    }
}

// 格式化日期
function formatDate(dateStr) {
    if (!dateStr) return '';
    return dateStr.replace(/(\d{4})(\d{2})(\d{2})/, '$1-$2-$3');
}

// 添加QR分页事件监听
document.getElementById('qr-prevPage').onclick = () => {
    if (qrCurrentPage > 1) {
        qrCurrentPage--;
        displayQRPage(qrCurrentPage);
        updateQRPagination(qrAllData.length);
    }
};

document.getElementById('qr-nextPage').onclick = () => {
    const totalPages = Math.ceil(qrAllData.length / qrPageSize);
    if (qrCurrentPage < totalPages) {
        qrCurrentPage++;
        displayQRPage(qrCurrentPage);
        updateQRPagination(qrAllData.length);
    }
};

