// 存储选中的文件
let selectedFiles = new Map();

// 添加 axios 拦截器初始化
function initAxiosInterceptors() {
    axios.interceptors.response.use(
        response => response,
        error => {
            if (error.response && error.response.status === 401) {
                window.location.href = '/login.html';
                return new Promise(() => {});
            }
            return Promise.reject(error);
        }
    );
}

// 页面加载完成后初始化
document.addEventListener('DOMContentLoaded', function() {
    initAxiosInterceptors();
    initDropZone();
    initFileInputs();
    loadStoreNodes();
    // 初始化 Toast
    storeToast = new bootstrap.Toast(document.getElementById('storeToast'), {
        delay: 3000
    });
});

// 初始化拖放区域
function initDropZone() {
    const dropZone = document.getElementById('dropZone');
    
    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
        dropZone.addEventListener(eventName, preventDefaults, false);
    });

    function preventDefaults(e) {
        e.preventDefault();
        e.stopPropagation();
    }

    ['dragenter', 'dragover'].forEach(eventName => {
        dropZone.addEventListener(eventName, highlight, false);
    });

    ['dragleave', 'drop'].forEach(eventName => {
        dropZone.addEventListener(eventName, unhighlight, false);
    });

    function highlight(e) {
        dropZone.classList.add('border-primary');
    }

    function unhighlight(e) {
        dropZone.classList.remove('border-primary');
    }

    dropZone.addEventListener('drop', handleDrop, false);
}

// 处理文件拖放
async function handleDrop(e) {
    const items = e.dataTransfer.items;
    const filePromises = [];

    for (let item of items) {
        if (item.kind === 'file') {
            const entry = item.webkitGetAsEntry();
            if (entry) {
                filePromises.push(processEntry(entry));
            }
        }
    }

    // 等待所有文件处理完成
    await Promise.all(filePromises);
    updateUI();
}

// 处理文件系统入口
async function processEntry(entry) {
    if (entry.isFile) {
        const file = await getFileFromEntry(entry);
        if (file.name.toLowerCase().endsWith('.dcm')) {
            addFileToSelection(file);
        }
    } else if (entry.isDirectory) {
        await processDirectory(entry);
    }
}

// 处理目录
async function processDirectory(dirEntry) {
    const entries = await readDirectoryEntries(dirEntry);
    const promises = entries.map(entry => processEntry(entry));
    await Promise.all(promises);
}

// 读取目录内容
function readDirectoryEntries(dirEntry) {
    return new Promise((resolve, reject) => {
        const reader = dirEntry.createReader();
        const entries = [];

        function readEntries() {
            reader.readEntries(async (results) => {
                if (results.length === 0) {
                    resolve(entries);
                } else {
                    entries.push(...results);
                    readEntries(); // 继续读取，直到没有更多条目
                }
            }, reject);
        }

        readEntries();
    });
}

// 从 FileEntry 获取 File 对象
function getFileFromEntry(entry) {
    return new Promise((resolve, reject) => {
        entry.file(resolve, reject);
    });
}

// 初始化文件输入
function initFileInputs() {
    const fileInput = document.getElementById('fileInput');
    const folderInput = document.getElementById('folderInput');
    const clearButton = document.getElementById('clearButton');
    const sendButton = document.getElementById('sendButton');

    // 文件选择处理
    fileInput.addEventListener('click', function(e) {
        e.preventDefault();
        const tempInput = document.createElement('input');
        tempInput.type = 'file';
        tempInput.multiple = true;
        tempInput.accept = '.dcm';
        tempInput.style.display = 'none';
        
        tempInput.addEventListener('change', handleFileSelect);
        document.body.appendChild(tempInput);
        tempInput.click();
        
        // 选择完成后移除临时元素
        setTimeout(() => {
            document.body.removeChild(tempInput);
        }, 100);
    });

    // 文件夹选择处理
    folderInput.addEventListener('click', function(e) {
        e.preventDefault();
        const tempInput = document.createElement('input');
        tempInput.type = 'file';
        tempInput.webkitdirectory = true;
        tempInput.directory = true;
        tempInput.style.display = 'none';
        
        tempInput.addEventListener('change', handleFileSelect);
        document.body.appendChild(tempInput);
        tempInput.click();
        
        // 选择完成后移除临时元素
        setTimeout(() => {
            document.body.removeChild(tempInput);
        }, 100);
    });

    clearButton.addEventListener('click', clearSelection);
    sendButton.addEventListener('click', sendFiles);
}

// 处理文件选择
function handleFileSelect(e) {
    const files = Array.from(e.target.files);
    let addedCount = 0;
    let skippedCount = 0;
    
    files.forEach(file => {
        if (file.name.toLowerCase().endsWith('.dcm')) {
            addFileToSelection(file);
            addedCount++;
        } else {
            skippedCount++;
        }
    });
    
    // 显示处理结果
    if (addedCount > 0 || skippedCount > 0) {
        showToast(`已添加 ${addedCount} 个DICOM文件${skippedCount > 0 ? `，跳过 ${skippedCount} 个非DICOM文件` : ''}`, true);
    }
    
    updateUI();
}

// 添加文件到选择列表
function addFileToSelection(file) {
    const fileId = generateFileId(file);
    if (!selectedFiles.has(fileId)) {
        if (file.size > 100 * 1024 * 1024) { // 100MB
            showToast(`文件 ${file.name} 太大，超过100MB`, false);
            return;
        }
        selectedFiles.set(fileId, {
            file: file,
            status: 'pending',
            message: ''
        });
        console.log('添加文件:', file.name, formatFileSize(file.size));
    }
}

// 生成文件ID
function generateFileId(file) {
    return `${file.name}-${file.size}-${file.lastModified}`;
}

// 更新UI显示
function updateUI() {
    const selectedFilesDiv = document.getElementById('selectedFiles');
    const fileList = document.getElementById('fileList');
    const sendButton = document.getElementById('sendButton');
    const clearButton = document.getElementById('clearButton');
    const totalFiles = selectedFiles.size;

    if (totalFiles > 0) {
        selectedFilesDiv.style.display = 'block';
        
        // 统计文件状态
        const stats = Array.from(selectedFiles.values()).reduce((acc, info) => {
            acc[info.status] = (acc[info.status] || 0) + 1;
            return acc;
        }, {});

        // 更新状态显示
        const statusInfo = document.createElement('div');
        statusInfo.className = 'mb-2';
        statusInfo.innerHTML = `
            总计: ${totalFiles} 个文件
            ${stats.pending ? `<span class="badge bg-secondary ms-2">待发送: ${stats.pending}</span>` : ''}
            ${stats.sending ? `<span class="badge bg-primary ms-2">发送中: ${stats.sending}</span>` : ''}
            ${stats.success ? `<span class="badge bg-success ms-2">已完成: ${stats.success}</span>` : ''}
            ${stats.error ? `<span class="badge bg-danger ms-2">失败: ${stats.error}</span>` : ''}
        `;
        
        // 替换或添加状态信息
        const existingStatus = selectedFilesDiv.querySelector('.status-info');
        if (existingStatus) {
            existingStatus.replaceWith(statusInfo);
        } else {
            selectedFilesDiv.insertBefore(statusInfo, selectedFilesDiv.firstChild);
        }
        statusInfo.classList.add('status-info');

        // 只有当有待发送或失败的文件时才启用发送按钮
        const hasPendingFiles = stats.pending > 0 || stats.error > 0;
        sendButton.disabled = !hasPendingFiles;

        // 文件列表
        fileList.innerHTML = Array.from(selectedFiles.entries())
            .map(([fileId, fileInfo]) => `
                <tr>
                    <td title="${fileInfo.file.name}">
                        ${fileInfo.file.name}
                    </td>
                    <td>${formatFileSize(fileInfo.file.size)}</td>
                    <td>
                        <span class="badge bg-${getStatusBadgeClass(fileInfo.status)}">
                            ${getStatusText(fileInfo.status)}
                        </span>
                        ${fileInfo.message ? 
                            `<br><small class="${fileInfo.status === 'error' ? 'text-danger' : 'text-muted'}">${fileInfo.message}</small>` : ''}
                    </td>
                    <td>
                        ${fileInfo.status !== 'sending' ? 
                            `<button type="button" class="btn btn-sm btn-danger" onclick="removeFile('${fileId}')">
                                删除
                            </button>` : ''}
                        ${fileInfo.status === 'error' ? 
                            `<button type="button" class="btn btn-sm btn-warning ms-1" onclick="retryFile('${fileId}')">
                                重试
                            </button>` : ''}
                    </td>
                </tr>
            `).join('');
    } else {
        selectedFilesDiv.style.display = 'none';
        sendButton.disabled = true;
        fileList.innerHTML = '';
    }

    // 清空按钮状态
    clearButton.disabled = totalFiles === 0 || Array.from(selectedFiles.values()).some(info => info.status === 'sending');
}

// 获取状态对应的Bootstrap样式类
function getStatusBadgeClass(status) {
    switch (status) {
        case 'pending': return 'secondary';
        case 'sending': return 'primary';
        case 'success': return 'success';
        case 'error': return 'danger';
        default: return 'secondary';
    }
}

// 获取状态文本
function getStatusText(status) {
    switch (status) {
        case 'pending': return '待发送';
        case 'sending': return '发送中';
        case 'success': return '已发送';
        case 'error': return '发送失败';
        default: return status;
    }
}

// 格式化文件大小
function formatFileSize(bytes) {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

// 移除文件
function removeFile(fileId) {
    selectedFiles.delete(fileId);
    updateUI();
}

// 清空选择
function clearSelection() {
    selectedFiles.clear();
    updateUI();
}

// 加载节点列表
async function loadStoreNodes() {
    try {
        const response = await axios.get('/api/StoreSCU/nodes');
        const nodes = response.data;
        
        const select = document.getElementById('storeNode');
        
        if (nodes.length === 0) {
            select.innerHTML = '<option value="">未配置DICOM节点</option>';
            return;
        }
        
        select.innerHTML = nodes.map(node => `
            <option value="${node.name}" ${node.isDefault ? 'selected' : ''}>
                ${node.name} (${node.aeTitle}@${node.hostName})
            </option>
        `).join('');

        // 如果有保存的选择，恢复它
        if (this.selectedNode) {
            select.value = this.selectedNode;
        } else {
            // 否则保存当前选择
            this.selectedNode = select.value;
        }

        // 监听节点选择变化
        select.addEventListener('change', (e) => {
            this.selectedNode = e.target.value;
        });

    } catch (error) {
        console.error('加载节点列表失败:', error);
        alert('加载节点列表失败，请检查网络连接');
    }
}

// 发送文件
async function sendFiles() {
    const nodeId = document.getElementById('storeNode').value;
    if (!nodeId) {
        showToast('请选择目标节点', false);
        return;
    }

    const sendButton = document.getElementById('sendButton');
    const clearButton = document.getElementById('clearButton');
    sendButton.disabled = true;
    clearButton.disabled = true;

    try {
        const pendingFiles = Array.from(selectedFiles.entries())
            .filter(([_, fileInfo]) => fileInfo.status === 'pending' || fileInfo.status === 'error');

        if (pendingFiles.length === 0) {
            showToast('没有需要发送的文件', false);
            return;
        }

        // 分批处理文件
        const batchSize = 10;
        const totalBatches = Math.ceil(pendingFiles.length / batchSize);
        let successCount = 0;
        let failureCount = 0;

        for (let i = 0; i < pendingFiles.length; i += batchSize) {
            const batch = pendingFiles.slice(i, i + batchSize);
            const formData = new FormData();
            
            batch.forEach(([_, fileInfo]) => {
                fileInfo.status = 'sending';
                formData.append('files', fileInfo.file);
            });
            updateUI();

            try {
                await axios.post(`/api/StoreSCU/send/${nodeId}`, formData);

                batch.forEach(([_, fileInfo]) => {
                    fileInfo.status = 'success';
                    successCount++;
                });
            } catch (error) {
                batch.forEach(([_, fileInfo]) => {
                    fileInfo.status = 'error';
                    fileInfo.message = error.message;
                    failureCount++;
                });
            }
            updateUI();
        }

        // 显示最终结果
        showToast(`发送完成：${successCount} 个成功${failureCount > 0 ? `，${failureCount} 个失败` : ''}`, failureCount === 0);

    } catch (error) {
        console.error('发送失败:', error);
        showToast('发送失败: ' + (error.response?.data || error.message), false);
    } finally {
        sendButton.disabled = false;
        clearButton.disabled = false;
        updateUI();
    }
}

// 添加重试功能
function retryFile(fileId) {
    const fileInfo = selectedFiles.get(fileId);
    if (fileInfo) {
        fileInfo.status = 'pending';
        fileInfo.message = '';
        updateUI();
    }
}

// 显示 Toast 消息
function showToast(message, isSuccess = true) {
    const toastEl = document.getElementById('storeToast');
    const titleEl = document.getElementById('storeToastTitle');
    const messageEl = document.getElementById('storeToastMessage');
    
    // 设置样式
    toastEl.classList.remove('bg-success', 'bg-danger', 'text-white');
    if (isSuccess) {
        toastEl.classList.add('bg-success', 'text-white');
        titleEl.textContent = '操作成功';
    } else {
        toastEl.classList.add('bg-danger', 'text-white');
        titleEl.textContent = '操作失败';
    }
    
    messageEl.textContent = message;
    storeToast.show();
}

class StoreManager {
    constructor() {
        this.selectedNode = null;  // 添加节点选择状态
        this.selectedFiles = new Map();
        this.init();
    }

    async init() {
        try {
            const response = await fetch('/api/StoreSCU/nodes');
            if (!response.ok) {
                throw new Error('获取节点列表失败');
            }
            this.nodes = await response.json();
            
            // 设置默认节点：优先使用配置中标记为默认的节点，如果没有则使用第一个节点
            this.defaultNode = this.nodes.find(n => n.isDefault) || this.nodes[0];
        } catch (error) {
            console.error('加载节点失败:', error);
        }
    }

    async sendFiles(files) {
        try {
            if (!files || files.length === 0) {
                throw new Error('请选择要发送的文件');
            }

            // 使用默认节点
            const remoteName = this.defaultNode?.name;
            if (!remoteName) {
                throw new Error('未找到可用的目标节点');
            }

            const formData = new FormData();
            for (const file of files) {
                formData.append('files', file);
            }

            const response = await fetch(`/api/StoreSCU/send/${remoteName}`, {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                throw new Error('发送文件失败');
            }

            const result = await response.json();
            console.log('发送成功:', result.message);
        } catch (error) {
            console.error('发送文件失败:', error);
        }
    }
}

// 创建全局实例
const storeManager = new StoreManager();
 