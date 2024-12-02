class ConfigManager {
    constructor() {
        this.init();
        this.helpModal = new bootstrap.Modal(document.getElementById('configHelpModal'));
    }

    init() {
        // 监听页面切换事件
        document.querySelectorAll('.nav-link[data-page]').forEach(link => {
            link.addEventListener('click', (e) => {
                if (e.target.getAttribute('data-page') === 'settings') {
                    this.loadConfig();
                }
            });
        });

        // 如果当前在设置页面，加载配置
        if (window.location.hash === '#settings') {
            this.loadConfig();
        }
    }

    async loadConfig() {
        try {
            const response = await fetch('/api/config');
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取配置失败');
            }

            const config = await response.text();
            const editor = document.getElementById('configEditor');
            if (editor) {
                editor.value = config;
            }
        } catch (error) {
            console.error('获取配置失败:', error);
            this.showToast('error', '获取配置失败: ' + error.message);
        }
    }

    async saveConfig() {
        try {
            // 验证JSON格式
            let config;
            try {
                config = JSON.parse(document.getElementById('configEditor').value);
            } catch (e) {
                this.showToast('error', '配置格式不正确，请检查JSON格式');
                return;
            }

            const response = await fetch('/api/config', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(config)
            });

            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }

            if (!response.ok) {
                throw new Error(await response.text());
            }

            // 只提示保存成功和需要重启
            this.showToast('success', '配置已保存，如需生效请点击右上角"重启服务"按钮');

        } catch (error) {
            console.error('保存配置失败:', error);
            this.showToast('error', '保存配置失败: ' + error.message);
        }
    }

    showToast(type, message) {
        const toast = document.getElementById('storeToast');
        const toastTitle = document.getElementById('storeToastTitle');
        const toastMessage = document.getElementById('storeToastMessage');
        
        toastTitle.textContent = type === 'error' ? '错误' : '成功';
        toastMessage.textContent = message;
        
        const bsToast = new bootstrap.Toast(toast);
        bsToast.show();
    }

    showHelp() {
        this.helpModal.show();
    }
}

// 初始化
window.configManager = new ConfigManager(); 