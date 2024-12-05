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
            this.updateServiceStatus('printScp', status.print);

            // 更新系统状态
            if (status.system) {
                const cpuStatus = document.getElementById('cpuStatus');
                const memoryStatus = document.getElementById('memoryStatus');
                const cpuUsage = status.system.cpuUsage;
                const memoryUsage = status.system.processMemory;
                
                // 更新导航栏显示
                if (cpuStatus) {
                    cpuStatus.textContent = `${cpuUsage.toFixed(1)}%`;
                    cpuStatus.parentElement.className = `status-item mb-1 ${
                        cpuUsage > 80 ? 'cpu-danger' :
                        cpuUsage > 50 ? 'cpu-warning' : 'cpu-normal'
                    }`;
                }
                
                if (memoryStatus) {
                    memoryStatus.textContent = `${memoryUsage.toFixed(0)}MB`;
                    memoryStatus.parentElement.className = `status-item ${
                        memoryUsage > 1024 ? 'memory-danger' :
                        memoryUsage > 512 ? 'memory-warning' : 'memory-normal'
                    }`;
                }

                // 更新模态框显示
                document.getElementById('modalCpuStatus').textContent = `${cpuUsage.toFixed(1)}%`;
                document.getElementById('modalCpuProgress').style.width = `${cpuUsage}%`;
                document.getElementById('modalCpuProgress').className = `progress-bar ${
                    cpuUsage > 80 ? 'bg-danger' :
                    cpuUsage > 50 ? 'bg-warning' : 'bg-success'
                }`;

                document.getElementById('modalSystemMemory').textContent = 
                    `${status.system.systemMemoryUsed.toFixed(0)}MB / ${status.system.systemMemoryTotal.toFixed(0)}MB (${status.system.systemMemoryPercent.toFixed(1)}%)`;
                document.getElementById('modalSystemMemoryProgress').style.width = `${status.system.systemMemoryPercent}%`;
                document.getElementById('modalSystemMemoryProgress').className = `progress-bar ${
                    status.system.systemMemoryPercent > 80 ? 'bg-danger' :
                    status.system.systemMemoryPercent > 60 ? 'bg-warning' : 'bg-success'
                }`;

                document.getElementById('modalProcessorInfo').textContent = 
                    `${status.system.processorCount}核 ${status.system.cpuModel}`;

                document.getElementById('modalProcessMemory').textContent = 
                    `${status.system.processMemory.toFixed(0)}MB`;

                // 修改运行时间计算
                const uptime = status.system.processStartTime;
                let uptimeText = '';
                if (uptime.days > 0) {
                    uptimeText += `${uptime.days}天`;
                }
                if (uptime.hours > 0 || uptime.days > 0) {
                    uptimeText += `${uptime.hours}小时`;
                }
                uptimeText += `${uptime.minutes}分钟`;

                document.getElementById('modalUptime').textContent = uptimeText;
                document.getElementById('modalOsVersion').textContent = status.system.platform;
            }
            
        } catch (error) {
            console.error('获取服务状态失败:', error);
            this.setAllServicesUnknown();
        }
    }

    updateServiceStatus(serviceId, status) {
        // 更新导航栏状态
        const element = document.getElementById(`${serviceId}-status`);
        if (element) {
            if (status.isRunning) {
                element.className = 'badge bg-success';
                element.textContent = '运行中';
            } else {
                element.className = 'badge bg-danger';
                element.textContent = '已停止';
            }
        }

        // 更新模态框状态
        const modalElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
        const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
        
        if (modalElement) {
            modalElement.className = `badge ${status.isRunning ? 'bg-success' : 'bg-danger'}`;
            modalElement.textContent = status.isRunning ? '运行中' : '已停止';
        }
        
        if (modalInfoElement) {
            modalInfoElement.textContent = `AET: ${status.aeTitle}  端口: ${status.port}`;
        }
    }

    setAllServicesUnknown() {
        ['storeScp', 'worklistScp', 'qrScp', 'printScp'].forEach(serviceId => {
            // 更新导航栏状态
            const element = document.getElementById(`${serviceId}-status`);
            if (element) {
                element.className = 'badge bg-warning';
                element.textContent = '未知';
            }

            // 更新模态框状态
            const modalElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
            const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
            
            if (modalElement) {
                modalElement.className = 'badge bg-warning';
                modalElement.textContent = '未知';
            }
            
            if (modalInfoElement) {
                modalInfoElement.textContent = 'AET: - 端口: -';
            }
        });
    }
}

// 初始化服务管理器
let serviceManager;
document.addEventListener('DOMContentLoaded', () => {
    serviceManager = new ServiceManager();
    
    // 系统状态点击事件
    document.querySelector('.system-status-group').addEventListener('click', () => {
        const systemInfoModal = new bootstrap.Modal(document.getElementById('systemInfoModal'));
        systemInfoModal.show();
    });

    // 服务状态点击事件
    document.querySelector('.service-status-group').addEventListener('click', () => {
        const serviceStatusModal = new bootstrap.Modal(document.getElementById('serviceStatusModal'));
        serviceStatusModal.show();
    });
});
