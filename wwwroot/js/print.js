class PrintManager {
    constructor() {
        this.allPrintJobs = [];  // 存储所有打印任务
        this.currentPage = 1;
        this.pageSize = 10;
        this.initializeEvents();
        this.loadPrintJobs();
    }

    initializeEvents() {
        // 绑定刷新按钮事件
        const refreshBtn = document.getElementById('print-refresh');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => this.loadPrintJobs());
        }

        // 绑定分页按钮事件
        const prevBtn = document.getElementById('print-prevPage');
        const nextBtn = document.getElementById('print-nextPage');
        
        if (prevBtn) {
            prevBtn.addEventListener('click', () => this.prevPage());
        }
        if (nextBtn) {
            nextBtn.addEventListener('click', () => this.nextPage());
        }
    }

    async loadPrintJobs() {
        try {
            const response = await fetch('/api/print');
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取打印任务失败');
            }
            
            const jobs = await response.json();
            this.allPrintJobs = jobs.sort((a, b) => 
                new Date(b.createTime) - new Date(a.createTime)
            );
            this.searchPrintJobs();  // 使用搜索函数来显示数据
        } catch (error) {
            console.error('加载打印任务失败:', error);
            this.showToast('错误', '加载打印任务失败: ' + error.message, 'danger');
        }
    }

    searchPrintJobs() {
        const patientId = document.getElementById('searchPrintPatientId')?.value?.toLowerCase() || '';
        const patientName = document.getElementById('searchPrintPatientName')?.value?.toLowerCase() || '';
        const accessionNumber = document.getElementById('searchPrintAccessionNumber')?.value?.toLowerCase() || '';
        const status = document.getElementById('searchPrintStatus')?.value || '';
        const searchDate = document.getElementById('searchPrintDate')?.value || '';

        // 过滤数据
        const filteredJobs = this.allPrintJobs.filter(job => {
            const jobDate = job.createTime ? new Date(job.createTime).toISOString().split('T')[0] : '';
            return (!patientId || job.patientId?.toLowerCase().includes(patientId)) &&
                   (!patientName || job.patientName?.toLowerCase().includes(patientName)) &&
                   (!accessionNumber || job.accessionNumber?.toLowerCase().includes(accessionNumber)) &&
                   (!status || job.status === status) &&
                   (!searchDate || jobDate === searchDate);
        });

        // 更新分页信息并显示当前页数据
        this.currentPage = 1;
        this.updatePrintJobsTable(filteredJobs);
    }

    resetSearch() {
        document.getElementById('printSearchForm').reset();
        this.currentPage = 1;
        this.updatePrintJobsTable(this.allPrintJobs);
    }

    updatePrintJobsTable(jobs) {
        // 计算当前页的数据
        const start = (this.currentPage - 1) * this.pageSize;
        const end = start + this.pageSize;
        const pageJobs = jobs.slice(start, end);

        const tbody = document.getElementById('print-jobs-table-body');
        if (!tbody) return;

        tbody.innerHTML = pageJobs.map(job => `
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

        // 更新分页信息
        this.updatePagination(jobs.length);
        // 更新作业统计
        this.updateJobStats(jobs);
    }

    updatePagination(total) {
        const totalPages = Math.ceil(total / this.pageSize);
        const start = (this.currentPage - 1) * this.pageSize + 1;
        const end = Math.min(this.currentPage * this.pageSize, total);

        document.getElementById('print-currentRange').textContent = total > 0 ? `${start}-${end}` : '0-0';
        document.getElementById('print-totalCount').textContent = total;
        document.getElementById('print-currentPage').textContent = this.currentPage;

        document.getElementById('print-prevPage').disabled = this.currentPage === 1;
        document.getElementById('print-nextPage').disabled = this.currentPage === totalPages || total === 0;
    }

    prevPage() {
        if (this.currentPage > 1) {
            this.currentPage--;
            const filteredJobs = this.getFilteredJobs();
            this.updatePrintJobsTable(filteredJobs);
        }
    }

    nextPage() {
        const total = this.getFilteredJobs().length;
        const totalPages = Math.ceil(total / this.pageSize);
        if (this.currentPage < totalPages) {
            this.currentPage++;
            const filteredJobs = this.getFilteredJobs();
            this.updatePrintJobsTable(filteredJobs);
        }
    }

    getFilteredJobs() {
        const patientId = document.getElementById('searchPrintPatientId')?.value?.toLowerCase() || '';
        const patientName = document.getElementById('searchPrintPatientName')?.value?.toLowerCase() || '';
        const accessionNumber = document.getElementById('searchPrintAccessionNumber')?.value?.toLowerCase() || '';
        const status = document.getElementById('searchPrintStatus')?.value || '';
        const searchDate = document.getElementById('searchPrintDate')?.value || '';

        const filteredJobs = this.allPrintJobs.filter(job => {
            const jobDate = job.createTime ? new Date(job.createTime).toISOString().split('T')[0] : '';
            return (!patientId || job.patientId?.toLowerCase().includes(patientId)) &&
                   (!patientName || job.patientName?.toLowerCase().includes(patientName)) &&
                   (!accessionNumber || job.accessionNumber?.toLowerCase().includes(accessionNumber)) &&
                   (!status || job.status === status) &&
                   (!searchDate || jobDate === searchDate);
        });

        return filteredJobs.sort((a, b) => 
            new Date(b.createTime) - new Date(a.createTime)
        );
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
        return date.toLocaleString('zh-CN', {
            year: 'numeric',
            month: '2-digit',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
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