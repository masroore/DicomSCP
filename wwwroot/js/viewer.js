let currentImageIndex = 0;
let imageIds = [];
const element = document.getElementById('viewer');
const urlParams = new URLSearchParams(window.location.search);
const studyUid = urlParams.get('studyUid');
const seriesUid = urlParams.get('seriesUid');
const baseUrl = window.location.origin;

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

// 初始化 Cornerstone
function initializeViewer() {
    try {
        // 配置 cornerstone
        cornerstoneWADOImageLoader.external.cornerstone = cornerstone;
        cornerstoneWADOImageLoader.external.dicomParser = dicomParser;
        
        // 配置图像加载器
        cornerstoneWADOImageLoader.configure({
            beforeSend: function(xhr) {
                xhr.setRequestHeader('Accept', 'application/dicom');
            },
            strict: false,
            useWebWorkers: false
        });
        
        // 启用 element
        cornerstone.enable(element);
        
        // 注册 WADO 图像加载器
        cornerstone.registerImageLoader('wadouri', cornerstoneWADOImageLoader.wadouri.loadImage);

        // 添加窗口大小改变事件监听
        window.addEventListener('resize', function() {
            cornerstone.resize(element);
        });

        // 添加鼠标滚轮事件监听
        element.addEventListener('wheel', function(event) {
            if (event.deltaY < 0) {
                displayImage(currentImageIndex - 1);
            } else {
                displayImage(currentImageIndex + 1);
            }
            event.preventDefault();
        });

        // 添加鼠标事件处理
        element.addEventListener('mousedown', function(e) {
            const lastX = e.pageX;
            const lastY = e.pageY;
            const viewport = cornerstone.getViewport(element);
            
            function mouseMoveHandler(e) {
                const deltaX = e.pageX - lastX;
                const deltaY = e.pageY - lastY;
                
                if (e.buttons === 1) { // 左键拖动 - 窗宽窗位
                    viewport.voi.windowWidth += (deltaX / 5);
                    viewport.voi.windowCenter += (deltaY / 5);
                } else if (e.buttons === 2) { // 右键拖动 - 平移
                    viewport.translation.x += (deltaX / viewport.scale);
                    viewport.translation.y += (deltaY / viewport.scale);
                }
                
                cornerstone.setViewport(element, viewport);
                e.preventDefault();
            }
            
            function mouseUpHandler() {
                document.removeEventListener('mousemove', mouseMoveHandler);
                document.removeEventListener('mouseup', mouseUpHandler);
            }
            
            document.addEventListener('mousemove', mouseMoveHandler);
            document.addEventListener('mouseup', mouseUpHandler);
        });

        // 禁用右键菜单
        element.addEventListener('contextmenu', function(e) {
            e.preventDefault();
        });

        // 添加键盘事件处理
        document.addEventListener('keydown', function(e) {
            switch(e.key) {
                case 'ArrowLeft':
                    displayImage(currentImageIndex - 1);
                    break;
                case 'ArrowRight':
                    displayImage(currentImageIndex + 1);
                    break;
                case 'Home':
                    displayImage(0);
                    break;
                case 'End':
                    displayImage(imageIds.length - 1);
                    break;
                case 'r':
                case 'R':
                    // 重置视图
                    cornerstone.reset(element);
                    break;
            }
        });

        // 添加滚轮缩放
        element.addEventListener('wheel', function(e) {
            if (e.shiftKey) {  // Shift + 滚轮进行缩放
                const viewport = cornerstone.getViewport(element);
                if (e.deltaY < 0) {
                    viewport.scale += 0.1;
                } else {
                    viewport.scale -= 0.1;
                }
                cornerstone.setViewport(element, viewport);
                e.preventDefault();
            }
        });

        console.log('[Init] Viewer initialized successfully');
    } catch (error) {
        console.error('[Init] Failed to initialize viewer:', error);
    }
}

// 在页面加载时初始化
initAxiosInterceptors();

// 加载序列图像
async function loadImages() {
    try {
        const response = await axios.get(`/api/images/${studyUid}/series/${seriesUid}/instances`);
        const instances = response.data;

        console.log('[Loading] Instances received:', instances);
        
        imageIds = instances.map(instance => {
            return `wadouri:${baseUrl}/api/images/download/${instance.sopInstanceUid}`;
        });

        if (imageIds.length > 0) {
            await displayImage(0);
        }
    } catch (error) {
        console.error('[Error] Failed to load images:', error);
        alert('Failed to load images');
    }
}

// 显示指定索引的图像
async function displayImage(index) {
    if (index < 0 || index >= imageIds.length) return;
    
    try {
        const image = await cornerstone.loadAndCacheImage(imageIds[index]);
        const viewport = cornerstone.getDefaultViewportForImage(element, image);
        
        // 设置默认窗宽窗位
        if (image.data.windowCenter !== undefined) {
            viewport.voi.windowCenter = image.data.windowCenter;
            viewport.voi.windowWidth = image.data.windowWidth;
        }
        
        cornerstone.displayImage(element, image, viewport);
        currentImageIndex = index;
        
        // 更新角落信息
        updateCornerInfo(image);
    } catch (error) {
        console.error('显示图像失败:', error);
    }
}

// 更���角落信息
function updateCornerInfo(image) {
    // 患者信息
    document.getElementById('patientInfo').innerHTML = `
        ${image.data.string('x00100010') || 'N/A'}<br>
        ID: ${image.data.string('x00100020') || 'N/A'}<br>
        性别: ${image.data.string('x00100040') || 'N/A'}
    `;

    // 检查信息
    document.getElementById('studyInfo').innerHTML = `
        检查号: ${image.data.string('x00080050') || 'N/A'}<br>
        检查类型: ${image.data.string('x00080060') || 'N/A'}<br>
        检查时间: ${formatDate(image.data.string('x00080020'))}
    `;

    // 图像信息
    document.getElementById('imageInfo').innerHTML = `
        序列号: ${image.data.string('x00200011') || 'N/A'}<br>
        图像号: ${image.data.string('x00200013') || 'N/A'}<br>
        ${currentImageIndex + 1}/${imageIds.length}
    `;

    // 窗宽窗位信息
    document.getElementById('windowInfo').innerHTML = `
        窗宽: ${Math.round(image.windowWidth)}<br>
        窗位: ${Math.round(image.windowCenter)}
    `;
}

// 格式化日期
function formatDate(dateStr) {
    if (!dateStr) return 'N/A';
    return `${dateStr.slice(0,4)}-${dateStr.slice(4,6)}-${dateStr.slice(6,8)}`;
}

// 初始化并加载图像
initializeViewer();
loadImages();