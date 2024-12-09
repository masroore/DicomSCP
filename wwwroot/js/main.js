// ================ 全局变量 ================

// 定义默认路由
const defaultRoute = 'images';  // 修改默认路由为影像管理页面

// ================ 通用工具函数 ================
// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    window.showToast(error.response?.data || error.message, 'error');
}

// 统一成功提示
function showSuccessMessage(message) {
    window.showToast(message, 'success');
}

// 显示加载状态
function showLoading(element) {
    if (!element) return;
    element.innerHTML = `
        <div class="d-flex justify-content-center align-items-center p-3">
            <div class="spinner-border text-primary" role="status">
                <span class="visually-hidden">加载中...</span>
            </div>
        </div>
    `;
}

// 统一的 Toast 显示函数
window.showToast = function(message, type = 'success') {
    try {
        const toastEl = document.getElementById('storeToast');
        if (!toastEl) {
            console.error('找不到 Toast 元素');
            return;
        }

        const titleEl = toastEl.querySelector('.toast-header strong');
        const messageEl = toastEl.querySelector('.toast-body');
        if (!titleEl || !messageEl) {
            console.error('找不到 Toast 内部元素');
            return;
        }

        // 设置标题和消息
        switch (type) {
            case 'success':
                titleEl.textContent = '操作成功';
                toastEl.classList.remove('bg-danger', 'bg-secondary');
                toastEl.classList.add('bg-success', 'text-white');
                break;
            case 'error':
                titleEl.textContent = '操作失败';
                toastEl.classList.remove('bg-success', 'bg-secondary');
                toastEl.classList.add('bg-danger', 'text-white');
                break;
            default:
                titleEl.textContent = '提示';
                toastEl.classList.remove('bg-success', 'bg-danger');
                toastEl.classList.add('bg-secondary', 'text-white');
        }

        messageEl.textContent = message;

        // 显示 Toast
        const toast = new bootstrap.Toast(toastEl, {
            animation: true,
            autohide: true,
            delay: 3000
        });
        toast.show();

    } catch (error) {
        // 避免递归调用
        console.error('显示提示失败:', error);
    }
};

// ================ 初始化函数 ================
// 初始化 axios 拦截器
function initAxiosInterceptors() {
    // 响应拦截器
    axios.interceptors.response.use(
        response => response,
        error => {
            if (error.response?.status === 401) {
                window.location.href = '/login.html';
            }
            return Promise.reject(error);
        }
    );
}

// 页面加载完成后执行
$(document).ready(function() {
    // 初始化 axios 拦截器
    initAxiosInterceptors();
    
    // 根据URL hash切换到对应页面
    const currentTab = window.location.hash.slice(1) || defaultRoute;
    switchPage(currentTab);
    
    // 导航链接点击事件
    $('.nav-link[data-page]').click(function(e) {
        e.preventDefault();
        const page = $(this).data('page');
        window.location.hash = page;
        switchPage(page);
    });

    // 获取当前登录用户名
    getCurrentUsername();
    
    // 添加版权信息到底部
    addCopyright();
});

// 添加版权信息
function addCopyright() {
    const footer = document.createElement('footer');
    footer.className = 'footer mt-auto py-3';
    footer.innerHTML = `
        <div class="container text-center">
            <span class="text-muted">
                © ${new Date().getFullYear()} DICOM管理系统 by 
                <a href="https://github.com/fightroad" target="_blank">
                    平凡之路 <i class="bi bi-github"></i>
                </a>
            </span>
        </div>
    `;
    document.body.appendChild(footer);

    // 添加样式以确保页脚始终在底部
    const style = document.createElement('style');
    style.textContent = `
        body {
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }
        .footer {
            margin-top: auto;
            background-color: #f8f9fa;
        }
        .footer a {
            color: inherit;
            text-decoration: none;
        }
        .footer a:hover {
            color: #0d6efd;
        }
    `;
    document.head.appendChild(style);
}

// ================ 页面切换 ================
// 切换页面函数
function switchPage(page) {
    try {
        // 隐藏所有页面
        $('#worklist-page, #images-page, #settings-page, #qr-page, #store-page, #logs-page, #print-page').hide();
        
        // 移除所有导航链接的active类
        $('.nav-link').removeClass('active');
        
        // 显示选中的页面
        $(`#${page}-page`).show();
        
        // 添加active类到当前导航链接
        $(`.nav-link[data-page="${page}"]`).addClass('active');

        // 关闭所有模态框
        ModalManager.closeAll();

        // 根据页面类型加载数据
        if (page === 'worklist') {
            loadWorklistData();
        } else if (page === 'images') {
            initializeImages();
        } else if (page === 'qr') {
            initializeQR();
        } else if (page === 'store') {
            if (typeof loadStoreNodes === 'function') {
                loadStoreNodes();
            }
        } else if (page === 'logs') {
            if (!window.logManager) {
                window.logManager = new LogManager();
            }
        }
    } catch (error) {
        console.error('切换页面失败:', error);
        window.showToast('页面切换失败', 'error');
    }
}

// ================ 用户相关函数 ================
// 登出
function logout() {
    axios.post('/api/auth/logout')
        .then(() => {
            window.location.href = '/login.html';
        })
        .catch(error => {
            console.error('登出失败:', error);
            window.location.href = '/login.html';  // 登出失败也跳转到登录页
        });
}

// ================ 对话框相关函数 ================
// 显示节点选择对话框
function showNodeSelectionDialog(nodeOptions) {
    return showDialog({
        title: '选择目标节点',
        content: `
            <select class="form-select" id="nodeSelect">
                ${nodeOptions.map((node, index) => 
                    `<option value="${index}">${node}</option>`
                ).join('')}
            </select>
        `,
        onConfirm: async () => {
            const select = document.getElementById('nodeSelect');
            return parseInt(select.value);
        }
    });
}

// 显示确认对话框
function showConfirmDialog(title, message) {
    return showDialog({
        title: title,
        content: `<p>${message}</p>`,
        onConfirm: async () => true
    });
}

// 全局模态框管理器
const ModalManager = {
    init() {
        // 监听所有模态框的隐藏事件
        $(document).on('hidden.bs.modal', '.modal', function() {
            // 移除所有焦点
            $(this).find('button, [role="button"], a, input, select, textarea').blur();
            
            // 如果是动态创建的模态框，清理它
            if ($(this).data('dynamic')) {
                const modal = bootstrap.Modal.getInstance(this);
                if (modal) {
                    modal.dispose();
                }
                $(this).remove();
            }
        });
    },

    // 关闭所有模态框
    closeAll() {
        $('.modal.show').each(function() {
            const modal = bootstrap.Modal.getInstance(this);
            if (modal) {
                modal.hide();
            }
        });
    }
};

// 初始化模态框管理器
document.addEventListener('DOMContentLoaded', () => {
    ModalManager.init();
});

// ================ 工具函数 ================
// 显示列表加载动画
function showTableLoading(tbody, colSpan = 6) {
    if (!tbody) return;
    tbody.innerHTML = `
        <tr>
            <td colspan="${colSpan}" class="text-center py-4">
                <div class="d-flex justify-content-center align-items-center">
                    <div class="spinner-border text-primary me-2" role="status">
                        <span class="visually-hidden">加载中...</span>
                    </div>
                    <span class="text-primary">正在加载数据...</span>
                </div>
            </td>
        </tr>
    `;
}

// 显示空数据提示
function showEmptyTable(tbody, message = '暂无数据', colSpan = 6) {
    if (!tbody) return;
    tbody.innerHTML = `
        <tr>
            <td colspan="${colSpan}" class="text-center py-4 text-muted">
                <i class="bi bi-inbox fs-2 mb-2 d-block"></i>
                ${message}
            </td>
        </tr>
    `;
}

// 通用对话框函数
function showDialog({ 
    title, 
    content, 
    onShow, 
    onConfirm,
    showFooter = true,
    size = '',
    fullHeight = false
}) {
    return new Promise((resolve) => {
        try {
            const modalDiv = document.createElement('div');
            modalDiv.className = 'modal';
            modalDiv.setAttribute('tabindex', '-1');
            modalDiv.setAttribute('data-dynamic', 'true');
            modalDiv.innerHTML = `
                <div class="modal-dialog ${size ? `modal-${size}` : ''} ${fullHeight ? 'modal-dialog-scrollable' : ''}">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">${title}</h5>
                            <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                        </div>
                        <div class="modal-body">
                            ${content}
                        </div>
                        ${showFooter ? `
                        <div class="modal-footer">
                            <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">取消</button>
                            <button type="button" class="btn btn-primary" id="dialogConfirm">确定</button>
                        </div>
                        ` : ''}
                    </div>
                </div>
            `;
            document.body.appendChild(modalDiv);

            const modal = new bootstrap.Modal(modalDiv);
            let isResolved = false;

            // 确认按钮点击事件
            const confirmButton = modalDiv.querySelector('#dialogConfirm');
            if (confirmButton) {
                confirmButton.addEventListener('click', async () => {
                    if (onConfirm) {
                        const result = await onConfirm();
                        if (result === false) return;
                    }
                    isResolved = true;
                    modal.hide();
                    resolve(true);
                });
            }

            // 监听关闭事件
            const handleHidden = () => {
                modalDiv.removeEventListener('hidden.bs.modal', handleHidden);
                const instance = bootstrap.Modal.getInstance(modalDiv);
                if (instance) {
                    instance.dispose();
                }
                if (document.body.contains(modalDiv)) {
                    modalDiv.remove();
                }
                if (!isResolved) {
                    resolve(false);
                }
            };

            modalDiv.addEventListener('hidden.bs.modal', handleHidden);

            // 显示对话框
            if (onShow) onShow();
            modal.show();

        } catch (error) {
            console.error('显示对话框失败:', error);
            resolve(false);
        }
    });
}

// 路由处理
function handleRoute() {
    const path = window.location.hash.slice(1) || defaultRoute;
    switchPage(path);
}


