class ConfigManager {
    constructor() {
        this.editor = null;
        this.helpModal = null;
        this.isLoading = false;
        this.initAxiosInterceptors();
        
        // 等待 DOM 加载完成后再初始化
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => this.init());
        } else {
            this.init();
        }
    }

    initAxiosInterceptors() {
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

    init() {
        this.editor = document.getElementById('configEditor');
       if (this.editor) {
            // 设置编辑器样式
            this.editor.style.backgroundColor = '#1e1e1e';  // VS Code 暗色主题背景色
            this.editor.style.color = '#d4d4d4';           // VS Code 暗色主题文本色
            this.editor.style.fontFamily = 'Consolas, Monaco, "Courier New", monospace';
            this.editor.style.fontSize = '14px';
            this.editor.style.lineHeight = '1.5';
            this.editor.style.padding = '12px';
            this.editor.style.border = '1px solid #2d2d2d';
            this.editor.style.borderRadius = '4px';
            this.editor.style.outline = 'none';
            this.editor.spellcheck = false;  // 禁用拼写检查
        }
        
        // 监听页面切换事件
        document.querySelectorAll('.nav-link[data-page]').forEach(link => {
            link.addEventListener('click', (e) => {
                if (e.target.getAttribute('data-page') === 'settings') {
                    this.loadConfig();
                }
            });
        });

        // 检查当前页面状态
        const settingsPage = document.getElementById('settings-page');
        if (settingsPage && (window.location.hash === '#settings' || 
            settingsPage.style.display !== 'none')) {
            // 延迟加载配置，确保页面已完全切换
            setTimeout(() => this.loadConfig(), 100);
        }

        // 添加 hashchange 事件监听
        window.addEventListener('hashchange', () => {
            if (window.location.hash === '#settings') {
                this.loadConfig();
            }
        });
    }

    async loadConfig() {
        if (this.isLoading) return;

        try {
            this.isLoading = true;
            const editor = document.getElementById('configEditor');
            if (!editor) return;

            showLoading(editor);
            const response = await axios.get('/api/config');
            editor.value = JSON.stringify(response.data, null, 2);

           // 恢复编辑器样式
            editor.style.backgroundColor = '#1e1e1e';
            editor.style.color = '#d4d4d4';

        } catch (error) {
            console.error('加载配置失败:', error);
            handleError(error, '获取配置失败');
        } finally {
            this.isLoading = false;
        }
    }

    async saveConfig() {
        if (this.isLoading) return;

        try {
            this.isLoading = true;
            // 先验证是否是有效的 JSON
            const configText = document.getElementById('configEditor').value;
            let config;
            try {
                config = JSON.parse(configText);
            } catch (e) {
                showToast('error', '验证失败', '配置格式不正确，请检查JSON格式');
                return;
            }

            await axios.post('/api/config', config);
            showSuccessMessage('配置保存成功！需要重启服务生效');
        } catch (error) {
            handleError(error, '保存配置失败');
        } finally {
            this.isLoading = false;
        }
    }

    showToast(type, message) {
        if (type === 'success') {
            showSuccessMessage(message);
        } else {
            handleError(new Error(message), '操作失败');
        }
    }

    async showHelp() {
        try {
            // 如果模态框不存在，创建它
            if (!this.helpModal) {
                // 加载 help.html 的内容
                const response = await fetch('help.html');
                const html = await response.text();

                // 创建模态框容器
                const modalDiv = document.createElement('div');
                modalDiv.className = 'modal fade';
                modalDiv.id = 'configHelpModal';
                modalDiv.setAttribute('tabindex', '-1');
                modalDiv.innerHTML = `
                    <div class="modal-dialog modal-lg modal-dialog-scrollable">
                        ${html}
                    </div>
                `;
                document.body.appendChild(modalDiv);

                // 初始化模态框
                this.helpModal = new bootstrap.Modal(modalDiv);

                // 监听模态框隐藏事件，清理DOM
                modalDiv.addEventListener('hidden.bs.modal', () => {
                    document.body.removeChild(modalDiv);
                    this.helpModal.dispose();
                    this.helpModal = null;
                });
            }

            // 显示模态框
            this.helpModal.show();
        } catch (error) {
            console.error('加载帮助内容失败:', error);
            showToast('error', '加载失败', '加载帮助内容失败');
        }
    }
}

// 创建全局实例
window.configManager = new ConfigManager(); 

