class PrintManager {
    constructor() {
        this.initializeEvents();
        this.loadPrintJobs();
    }

    initializeEvents() {
        // 绑定刷新按钮事件
        const refreshBtn = document.getElementById('print-refresh');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => this.loadPrintJobs());
        }
    }

    async loadPrintJobs() {
        try {
            const response = await fetch('/api/print/jobs');
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取打印作业失败');
            }
            
            const jobs = await response.json();
            this.updatePrintJobsTable(jobs);
        } catch (error) {
            console.error('加载打印作业失败:', error);
            this.showToast('错误', '加载打印作业失败: ' + error.message, 'danger');
        }
    }

    updatePrintJobsTable(jobs) {
        const tbody = document.getElementById('print-jobs-table-body');
        if (!tbody) return;

        tbody.innerHTML = jobs.map(job => `
            <tr>
                <td>${job.jobId}</td>
                <td>${job.patientName || ''}</td>
                <td>${job.patientId || ''}</td>
                <td>${job.accessionNumber || ''}</td>
                <td>${this.formatDateTime(job.createTime)}</td>
                <td>
                    <span class="badge ${this.getStatusBadgeClass(job.status)}">
                        ${this.formatStatus(job.status)}
                    </span>
                </td>
                <td>
                    ${this.getActionButtons(job)}
                </td>
            </tr>
        `).join('');

        // 更新作业统计
        this.updateJobStats(jobs);
    }

    updateJobStats(jobs) {
        const stats = jobs.reduce((acc, job) => {
            acc[job.status] = (acc[job.status] || 0) + 1;
            return acc;
        }, {});

        const statsContainer = document.getElementById('print-jobs-stats');
        if (statsContainer) {
            statsContainer.innerHTML = `
                <span class="badge bg-secondary me-2">总数: ${jobs.length}</span>
                ${stats.PENDING ? `<span class="badge bg-warning me-2">待处理: ${stats.PENDING}</span>` : ''}
                ${stats.PRINTING ? `<span class="badge bg-primary me-2">打印中: ${stats.PRINTING}</span>` : ''}
                ${stats.COMPLETED ? `<span class="badge bg-success me-2">已完成: ${stats.COMPLETED}</span>` : ''}
                ${stats.FAILED ? `<span class="badge bg-danger me-2">失败: ${stats.FAILED}</span>` : ''}
            `;
        }
    }

    getStatusBadgeClass(status) {
        switch (status) {
            case 'PENDING': return 'bg-warning';
            case 'PRINTING': return 'bg-primary';
            case 'COMPLETED': return 'bg-success';
            case 'FAILED': return 'bg-danger';
            default: return 'bg-secondary';
        }
    }

    formatStatus(status) {
        const statusMap = {
            'PENDING': '待处理',
            'PRINTING': '打印中',
            'COMPLETED': '已完成',
            'FAILED': '失败'
        };
        return statusMap[status] || status;
    }

    getActionButtons(job) {
        const buttons = [];
        
        if (job.status === 'PENDING' || job.status === 'FAILED') {
            buttons.push(`
                <button class="btn btn-sm btn-primary me-1" 
                    onclick="printManager.startPrintJob('${job.jobId}')">
                    打印
                </button>
            `);
        }
        
        if (job.status !== 'COMPLETED') {
            buttons.push(`
                <button class="btn btn-sm btn-danger" 
                    onclick="printManager.deletePrintJob('${job.jobId}')">
                    删除
                </button>
            `);
        }
        
        return buttons.join('');
    }

    formatDateTime(dateTimeStr) {
        if (!dateTimeStr) return '';
        const date = new Date(dateTimeStr);
        return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')} ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}:${String(date.getSeconds()).padStart(2, '0')}`;
    }

    async startPrintJob(jobId) {
        try {
            const response = await fetch(`/api/print/jobs/${jobId}/start`, {
                method: 'POST'
            });

            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }

            if (!response.ok) {
                throw new Error('启动打印作业失败');
            }

            this.showToast('成功', '打印作业已启动');
            await this.loadPrintJobs();
        } catch (error) {
            console.error('启动打印作业失败:', error);
            this.showToast('错误', '启动打印作业失败: ' + error.message, 'danger');
        }
    }

    async deletePrintJob(jobId) {
        if (!confirm('确定要删除此打印作业吗？')) {
            return;
        }

        try {
            const response = await fetch(`/api/print/jobs/${jobId}`, {
                method: 'DELETE'
            });

            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }

            if (!response.ok) {
                throw new Error('删除打印作业失败');
            }

            this.showToast('成功', '打印作业已删除');
            await this.loadPrintJobs();
        } catch (error) {
            console.error('删除打印作业失败:', error);
            this.showToast('错误', '删除打印作业失败: ' + error.message, 'danger');
        }
    }

    showToast(title, message, type = 'success') {
        const toast = document.getElementById('printToast');
        const toastTitle = document.getElementById('printToastTitle');
        const toastMessage = document.getElementById('printToastMessage');
        
        if (toast && toastTitle && toastMessage) {
            toastTitle.textContent = title;
            toastMessage.textContent = message;
            
            const bsToast = new bootstrap.Toast(toast);
            bsToast.show();
        }
    }
}

// 初始化打印管理器
let printManager;
document.addEventListener('DOMContentLoaded', () => {
    printManager = new PrintManager();
}); 