// ================ 全局变量 ================
let changePasswordModal;
let viewerModal;

// ================ 通用工具函数 ================
// 统一错误处理
function handleError(error, message) {
    console.error(message, error);
    showToast('error', '操作失败', error.response?.data || error.message);
}

// 统一成功提示
function showSuccessMessage(message) {
    showToast('success', '操作成功', message);
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

// 添加通用的提示框显示函数
function showToast(type, title, message) {
    try {
        const toastEl = document.getElementById('storeToast');
        const titleEl = document.getElementById('storeToastTitle');
        const messageEl = document.getElementById('storeToastMessage');
        
        toastEl.classList.remove('bg-success', 'bg-danger', 'text-white');
        toastEl.classList.add(type === 'success' ? 'bg-success' : 'bg-danger', 'text-white');
        titleEl.textContent = title;
        messageEl.textContent = message;
        
        const storeToast = new bootstrap.Toast(toastEl);
        storeToast.show();
    } catch (error) {
        console.error('显示提示失败:', error);
    }
}

// 修改所有模态框的清理代码
function cleanupModal(modalId, callback) {
    return () => {
        const modalElement = document.getElementById(modalId);
        if (modalElement && document.body.contains(modalElement)) {
            modalElement.remove();
        }
        if (callback) {
            delete window[callback];
        }
    };
}

// 添加到通用工具函数部分
function handleApiError(error, defaultMessage) {
    if (error.response) {
        // 服务器返回错误
        if (error.response.status === 404) {
            handleError(error, '请求的资源不存在');
        } else {
            handleError(error, error.response.data || defaultMessage);
        }
    } else if (error.request) {
        // 请求发送失败
        handleError(error, '网络连接失败，请检查网络');
    } else {
        // 其他错误
        handleError(error, defaultMessage);
    }
}

// ================ 初始化函数 ================
// 初始化 axios 拦截器
function initAxiosInterceptors() {
    axios.interceptors.response.use(
        response => response,  // 正常响应直接返回
        error => {
            if (error.response && error.response.status === 401) {
                // 未登录或会话过期，重定向到登录页
                console.log("[Auth] 检测到未授权访问，重定向到登录页");
                window.location.href = '/login.html';
                // 阻止显示其他错误提示
                return new Promise(() => {});
            }
            return Promise.reject(error);
        }
    );

    // 添加请求拦截器
    axios.interceptors.request.use(
        config => config,
        error => Promise.reject(error)
    );
}

// 页面加载完成后执行
$(document).ready(function() {
    // 初始化 axios 拦截器
    initAxiosInterceptors();
    
    // 初始化各种模态框和其他组件
    initializeComponents();
    
    // 初始化预约模块
    initializeWorklist();
    
    // 根据URL hash切换到对应页面，如没有hash则默认显示worklist
    const currentTab = window.location.hash.slice(1) || 'worklist';
    switchPage(currentTab);
    
    // 导航链接点击事件
    $('.nav-link[data-page]').click(function(e) {
        e.preventDefault();
        const page = $(this).data('page');
        window.location.hash = page; // 更新URL hash
        switchPage(page);
    });

    // 获取当前登录用户名
    getCurrentUsername();
});

// 初始化组件
function initializeComponents() {
    // 初始化修改密码模态框
    const changePasswordModalElement = document.getElementById('changePasswordModal');
    if (changePasswordModalElement) {
        changePasswordModal = new bootstrap.Modal(changePasswordModalElement);
    }
    
    // 初始化查看器模态框
    const viewerModalElement = document.getElementById('viewerModal');
    if (viewerModalElement) {
        viewerModal = new bootstrap.Modal(viewerModalElement);
        
        // 监听模态框关闭事件，清理资源
        viewerModalElement.addEventListener('hidden.bs.modal', function () {
            const viewerFrame = document.getElementById('viewerFrame');
            if (viewerFrame) {
                viewerFrame.src = 'about:blank';
            }
        });
    }
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

        // 关闭所有打开的模态框
        $('.modal.show').each(function() {
            $(this).modal('hide');
        });
        
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
            // 初始化日志管理器
            if (!window.logManager) {
                window.logManager = new LogManager();
            }
        }
    } catch (error) {
        console.error('切换页面失败:', error);
        showToast('error', '切换失败', '页面切换失败');
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

// 获取当前用户名
async function getCurrentUsername() {
    try {
        const response = await axios.get('/api/auth/check-session');
        const username = response.data.username;
        document.getElementById('currentUsername').textContent = username;
    } catch (error) {
        // 401 错误会被全局拦截器处理，这里只处理其他错误
        if (error.response?.status !== 401) {
            console.error('获取用户信息失败:', error);
            document.getElementById('currentUsername').textContent = '未知用户';
        }
    }
}

// 修改密码
async function changePassword() {
    const oldPassword = document.getElementById('oldPassword').value;
    const newPassword = document.getElementById('newPassword').value;
    const confirmPassword = document.getElementById('confirmPassword').value;

    // 验证新密码
    if (newPassword !== confirmPassword) {
        showToast('error', '验证失败', '两次输入的新密码不一致');
        return;
    }

    // 验证新密码长度
    if (newPassword.length < 6) {
        showToast('error', '验证失败', '新密码长度不能少于6位');
        return;
    }

    try {
        await axios.post('/api/auth/change-password', {
            oldPassword: oldPassword,
            newPassword: newPassword
        });
        
        showToast('success', '操作成功', '密码修改成功，请重新登录');
        changePasswordModal.hide();
        setTimeout(() => {
            window.location.href = '/login.html';
        }, 1500);
    } catch (error) {
        showToast('error', '修改失败', error.response?.data || '修改密码失败，请重试');
    }
}

// 显示修改密码模态框
function showChangePasswordModal() {
    document.getElementById('changePasswordForm').reset();
    changePasswordModal.show();
}

// ================ 对话框相关函数 ================
// 显示节点选择对话框
function showNodeSelectionDialog(nodeOptions) {
    return new Promise((resolve) => {
        try {
            // 移除旧的对话框（如果存在）
            const oldModal = document.getElementById('nodeSelectModal');
            if (oldModal && document.body.contains(oldModal)) {
                const oldInstance = bootstrap.Modal.getInstance(oldModal);
                if (oldInstance) {
                    oldInstance.dispose();
                }
                oldModal.remove();
            }

            const html = `
                <div class="modal fade" id="nodeSelectModal" tabindex="-1" role="dialog" aria-modal="true" aria-labelledby="nodeSelectTitle">
                    <div class="modal-dialog" role="document">
                        <div class="modal-content">
                            <div class="modal-header">
                                <h5 class="modal-title" id="nodeSelectTitle">选择目标节点</h5>
                                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="关闭"></button>
                            </div>
                            <div class="modal-body">
                                <div class="mb-3">
                                    <label class="form-label">请选择要发送到的PACS节点：</label>
                                    <select class="form-select" id="nodeSelect">
                                        ${nodeOptions.map((node, index) => 
                                            `<option value="${index}">${node}</option>`
                                        ).join('')}
                                    </select>
                                </div>
                            </div>
                            <div class="modal-footer">
                                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">取消</button>
                                <button type="button" class="btn btn-primary" id="nodeSelectConfirm">发送</button>
                            </div>
                        </div>
                    </div>
                </div>
            `;

            document.body.insertAdjacentHTML('beforeend', html);
            const modalElement = document.getElementById('nodeSelectModal');
            if (!modalElement) {
                throw new Error('无法创建节点选择对话框');
            }

            let isResolved = false;
            let modalInstance = null;

            const cleanup = () => {
                try {
                    if (modalInstance) {
                        modalInstance.dispose();
                    }
                    if (modalElement && document.body.contains(modalElement)) {
                        modalElement.remove();
                    }
                } catch (error) {
                    console.error('清理对话框失败:', error);
                }
            };

            modalInstance = new bootstrap.Modal(modalElement, {
                backdrop: 'static',
                keyboard: false
            });

            const confirmButton = modalElement.querySelector('#nodeSelectConfirm');
            if (confirmButton) {
                confirmButton.addEventListener('click', () => {
                    try {
                        const select = document.getElementById('nodeSelect');
                        const selectedIndex = parseInt(select.value);
                        isResolved = true;
                        modalInstance.hide();
                        resolve(selectedIndex);
                    } catch (error) {
                        console.error('确认选择失败:', error);
                        cleanup();
                        resolve(-1);
                    }
                });
            }

            modalElement.addEventListener('hidden.bs.modal', () => {
                try {
                    if (!isResolved) {
                        resolve(-1);
                    }
                    cleanup();
                } catch (error) {
                    console.error('处理对话框关闭事件失败:', error);
                    resolve(-1);
                }
            });

            modalInstance.show();

        } catch (error) {
            console.error('显示节点选择对话框失败:', error);
            resolve(-1);
        }
    });
}

// 添加确认对话框函数
function showConfirmMoveDialog() {
    return showConfirmDialog('确认回取图像', '确定要回取这个检查/序列的图像吗？');
}

// 修改确认对话框函数
function showConfirmDialog(title, message) {
    return new Promise((resolve) => {
        try {
            // 移除旧的对话框（如果存在）
            const oldModal = document.getElementById('confirmDialog');
            if (oldModal && document.body.contains(oldModal)) {
                const oldInstance = bootstrap.Modal.getInstance(oldModal);
                if (oldInstance) {
                    oldInstance.dispose();
                }
                oldModal.remove();
            }

            const html = `
                <div class="modal fade" id="confirmDialog" tabindex="-1" role="dialog" aria-modal="true" aria-labelledby="confirmDialogTitle">
                    <div class="modal-dialog" role="document">
                        <div class="modal-content">
                            <div class="modal-header">
                                <h5 class="modal-title" id="confirmDialogTitle">${title}</h5>
                                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="关闭"></button>
                            </div>
                            <div class="modal-body">
                                <p>${message}</p>
                            </div>
                            <div class="modal-footer">
                                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">取消</button>
                                <button type="button" class="btn btn-primary" id="confirmDialogConfirm">确定</button>
                            </div>
                        </div>
                    </div>
                </div>
            `;

            document.body.insertAdjacentHTML('beforeend', html);
            const modalElement = document.getElementById('confirmDialog');
            if (!modalElement) {
                throw new Error('无法创建确认对话框');
            }

            let isResolved = false;
            let modalInstance = null;

            const cleanup = () => {
                try {
                    if (modalInstance) {
                        modalInstance.dispose();
                    }
                    if (modalElement && document.body.contains(modalElement)) {
                        modalElement.remove();
                    }
                } catch (error) {
                    console.error('清理对话框失败:', error);
                }
            };

            modalInstance = new bootstrap.Modal(modalElement, {
                backdrop: 'static',
                keyboard: false
            });

            // 使用事件监听而不是全局函数
            const confirmButton = modalElement.querySelector('#confirmDialogConfirm');
            if (confirmButton) {
                confirmButton.addEventListener('click', () => {
                    try {
                        isResolved = true;
                        modalInstance.hide();
                        resolve(true);
                    } catch (error) {
                        console.error('确认按钮点击处理失败:', error);
                        cleanup();
                        resolve(false);
                    }
                });
            }

            modalElement.addEventListener('hidden.bs.modal', () => {
                try {
                    if (!isResolved) {
                        resolve(false);
                    }
                    cleanup();
                } catch (error) {
                    console.error('处理对话框关闭事件失败:', error);
                    resolve(false);
                }
            });

            modalInstance.show();

            // 设置焦点到确定按钮
            if (confirmButton) {
                confirmButton.focus();
            }

        } catch (error) {
            console.error('显示确认对话框失败:', error);
            resolve(false);
        }
    });
}

// 添加模态框管理器
const ModalManager = {
    activeModals: new Set(),

    show(modalId, options = {}) {
        const modal = new bootstrap.Modal(document.getElementById(modalId), options);
        this.activeModals.add(modalId);
        modal.show();
        return modal;
    },

    hide(modalId) {
        const modalElement = document.getElementById(modalId);
        if (modalElement) {
            const modal = bootstrap.Modal.getInstance(modalElement);
            if (modal) {
                modal.hide();
            }
        }
        this.activeModals.delete(modalId);
    },

    cleanup(modalId) {
        const modalElement = document.getElementById(modalId);
        if (modalElement && document.body.contains(modalElement)) {
            modalElement.remove();
        }
        this.activeModals.delete(modalId);
    },

    hideAll() {
        this.activeModals.forEach(modalId => this.hide(modalId));
    }
};

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


