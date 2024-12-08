class ConfigManager {
    constructor() {
        this.editor = null;
        this.isLoading = false;
        
        // 等待 DOM 加载完成后再初始化
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => this.init());
        } else {
            this.init();
        }
    }

    init() {
        this.editor = document.getElementById('configEditor');
        if (this.editor) {
            // 设置编辑器样式
            this.editor.style.backgroundColor = '#1e1e1e';
            this.editor.style.color = '#d4d4d4';
            this.editor.style.fontFamily = 'Consolas, Monaco, "Courier New", monospace';
            this.editor.style.fontSize = '14px';
            this.editor.style.lineHeight = '1.5';
            this.editor.style.padding = '12px';
            this.editor.style.border = '1px solid #2d2d2d';
            this.editor.style.borderRadius = '4px';
            this.editor.style.outline = 'none';
            this.editor.spellcheck = false;
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
            window.showToast(error.response?.data || '获取配置失败', 'error');
        } finally {
            this.isLoading = false;
        }
    }

    async saveConfig() {
        if (this.isLoading) return;

        try {
            this.isLoading = true;
            const configText = document.getElementById('configEditor').value;
            let config;
            try {
                config = JSON.parse(configText);
            } catch (e) {
                window.showToast('配置格式不正确，请检查JSON格式', 'error');
                return;
            }

            await axios.post('/api/config', config);
            window.showToast('配置保存成功！需要重启服务生效', 'success');
        } catch (error) {
            window.showToast(error.response?.data || '保存配置失败', 'error');
        } finally {
            this.isLoading = false;
        }
    }

    async showHelp() {
        try {
            const response = await fetch('help.html');
            const html = await response.text();

            const modalDiv = document.createElement('div');
            modalDiv.className = 'modal';
            modalDiv.setAttribute('tabindex', '-1');
            modalDiv.setAttribute('data-dynamic', 'true');
            modalDiv.innerHTML = `
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    ${html}
                </div>
            `;
            document.body.appendChild(modalDiv);

            const modal = new bootstrap.Modal(modalDiv);

            // 监听关闭事件
            const handleHidden = () => {
                // 移除事件监听
                modalDiv.removeEventListener('hidden.bs.modal', handleHidden);
                
                // 确保模态框实例还存在
                const instance = bootstrap.Modal.getInstance(modalDiv);
                if (instance) {
                    instance.dispose();
                }
                
                // 从 DOM 中移除
                if (document.body.contains(modalDiv)) {
                    modalDiv.remove();
                }
            };

            modalDiv.addEventListener('hidden.bs.modal', handleHidden);

            modal.show();
        } catch (error) {
            console.error('加载帮助内容失败:', error);
            window.showToast('加载帮助内容失败', 'error');
        }
    }
}

// 创建全局实例
window.configManager = new ConfigManager();

