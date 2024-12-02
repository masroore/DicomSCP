class ServiceManager {
    constructor() {
        this.updateStatus();
        // 每30秒更新一次状态
        setInterval(() => this.updateStatus(), 30000);
    }

    async updateStatus() {
        try {
            const response = await fetch('/api/dicom/status');
            if (response.status === 401) {
                // 未授权，重定向到登录页
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取服务状态失败');
            }
            
            const status = await response.json();
            
            // 更新每个服务的状态
            this.updateServiceStatus('storeScp', status.store);
            this.updateServiceStatus('worklistScp', status.worklist);
            this.updateServiceStatus('qrScp', status.qr);
            
        } catch (error) {
            console.error('获取服务状态失败:', error);
            this.setAllServicesUnknown();
        }
    }

    updateServiceStatus(serviceId, status) {
        const element = document.getElementById(`${serviceId}-status`);
        if (element) {
            if (status.isRunning) {
                element.className = 'badge bg-success';
                element.textContent = '运行中';
            } else {
                element.className = 'badge bg-danger';
                element.textContent = '已停止';
            }
            
            // 移除旧的 tooltip
            const oldTooltip = bootstrap.Tooltip.getInstance(element);
            if (oldTooltip) {
                oldTooltip.dispose();
            }
            
            // 创建新的 tooltip，使用更清晰的格式
            new bootstrap.Tooltip(element, {
                title: `AET: ${status.aeTitle}  端口: ${status.port}`,
                placement: 'bottom'
            });
        }
    }

    setAllServicesUnknown() {
        ['storeScp', 'worklistScp', 'qrScp'].forEach(serviceId => {
            const element = document.getElementById(`${serviceId}-status`);
            if (element) {
                element.className = 'badge bg-warning';
                element.textContent = '未知';
            }
        });
    }

    async restartAllServices() {
        if (!confirm('确定要重启所有服务吗？这可能会导致正在进行的操作中断。')) {
            return;
        }

        try {
            // 显示所有服务为重启中状态
            ['storeScp', 'worklistScp', 'qrScp'].forEach(serviceId => {
                const element = document.getElementById(`${serviceId}-status`);
                if (element) {
                    element.className = 'badge bg-warning';
                    element.textContent = '重启中...';
                }
            });

            const response = await fetch('/api/dicom/restart', {
                method: 'POST'
            });

            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('重启服务失败');
            }

            // 等待几秒后更新状态
            setTimeout(() => this.updateStatus(), 5000);
        } catch (error) {
            console.error('重启服务失败:', error);
            alert('重启服务失败: ' + error.message);
            this.updateStatus();
        }
    }
}

// 初始化服务管理器
let serviceManager;
document.addEventListener('DOMContentLoaded', () => {
    serviceManager = new ServiceManager();
});