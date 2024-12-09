console.log('viewer.js loaded');

let currentImageIndex = 0;
let imageIds = [];
let currentTool = 'Wwwc';
const element = document.getElementById('viewer');
console.log('viewer element:', element);

const urlParams = new URLSearchParams(window.location.search);
const studyUid = urlParams.get('studyUid');
const seriesUid = urlParams.get('seriesUid');
const baseUrl = window.location.origin;

// 初始化 Cornerstone
function initializeViewer() {
    try {
        // 配置 cornerstone
        cornerstoneTools.external.cornerstone = cornerstone;
        cornerstoneTools.external.Hammer = Hammer;
        
        // 配置图像加载器
        cornerstoneWADOImageLoader.external.cornerstone = cornerstone;
        cornerstoneWADOImageLoader.external.dicomParser = dicomParser;
        cornerstoneWADOImageLoader.configure({
            useWebWorkers: false
        });

        // 启用 element
        cornerstone.enable(element);

        // 初始化工具
        initializeTools();
        
        // 设置工具栏事件
        setupToolbar();

        // 添加图像渲染事件监听
        cornerstone.events.addEventListener('cornerstoneimagerendered', onImageRendered);

        // 添加滚轮事件
        element.addEventListener('wheel', function(event) {
            if (event.deltaY < 0) {
                displayImage(currentImageIndex - 1);
            } else {
                displayImage(currentImageIndex + 1);
            }
            event.preventDefault();
        });

        console.log('[Init] Viewer initialized successfully');
    } catch (error) {
        console.error('[Init] Failed to initialize viewer:', error);
    }
}

// 等待 DOM 加载完成后再初始化
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeViewer);
} else {
    initializeViewer();
}

// 初始化工具
function initializeTools() {
    cornerstoneTools.init();

    // 添加工具
    cornerstoneTools.addTool(cornerstoneTools.WwwcTool);
    cornerstoneTools.addTool(cornerstoneTools.PanTool);
    cornerstoneTools.addTool(cornerstoneTools.ZoomTool, {
        configuration: {
            minScale: 0.3,
            maxScale: 10,
            preventZoomOutside: true
        }
    });
    cornerstoneTools.addTool(cornerstoneTools.LengthTool);
    cornerstoneTools.addTool(cornerstoneTools.AngleTool);
    cornerstoneTools.addTool(cornerstoneTools.RectangleRoiTool);
    cornerstoneTools.addTool(cornerstoneTools.EllipticalRoiTool);

    // 设置默认工具
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });

    // 配置工具样式
    cornerstoneTools.toolStyle.setToolWidth(2);
    cornerstoneTools.toolColors.setToolColor('rgb(255, 255, 0)');
    cornerstoneTools.toolColors.setActiveColor('rgb(0, 255, 0)');

    // 禁用右键菜单
    element.addEventListener('contextmenu', function(e) {
        e.preventDefault();
    });

    // 添加滚轮事件
    element.addEventListener('wheel', function(e) {
        if (e.shiftKey) {  // Shift + 滚轮进行缩放
            e.preventDefault();
            const viewport = cornerstone.getViewport(element);
            if (e.deltaY < 0) {
                viewport.scale *= 1.1;
            } else {
                viewport.scale *= 0.9;
            }
            viewport.scale = Math.max(0.3, Math.min(10, viewport.scale));
            cornerstone.setViewport(element, viewport);
        } else {  // 普通滚轮切换图像
            if (e.deltaY < 0) {
                displayImage(currentImageIndex - 1);
            } else {
                displayImage(currentImageIndex + 1);
            }
            e.preventDefault();
        }
    });
}

// 激活工具
function activateTool(toolName) {
    const toolMap = {
        'wwwc': 'Wwwc',
        'pan': 'Pan',
        'zoom': 'Zoom',
        'length': 'Length',
        'angle': 'Angle',
        'rectangle': 'RectangleRoi',
        'ellipse': 'EllipticalRoi'
    };

    const toolToActivate = toolMap[toolName];
    if (toolToActivate) {
        try {
            // 禁用所有工具
            Object.values(toolMap).forEach(tool => {
                cornerstoneTools.setToolPassive(tool);
            });

            // 激活��中的工具
            if (toolName === 'zoom') {
                // 缩放工具使用左键
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 1 });
                // 保持平移可用（右键）
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
            } else if (toolName === 'pan') {
                // 平移工具使用左键
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 1 });
                // 保持缩放可用（右键）
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 2 });
            } else {
                // 其他工具使用左键
                cornerstoneTools.setToolActive(toolToActivate, { mouseButtonMask: 1 });
                // 保持平移（右键）和缩放（中键）可用
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
            }

            currentTool = toolToActivate;
            console.log('Tool activated:', toolToActivate);
        } catch (error) {
            console.error('Error activating tool:', error);
        }
    }
}

// 设置工具栏
function setupToolbar() {
    document.querySelectorAll('.tool-button').forEach(button => {
        button.addEventListener('click', handleToolButtonClick);
    });
}

// 工具按钮点击处理
function handleToolButtonClick(e) {
    e.preventDefault();
    e.stopPropagation();

    const toolName = this.dataset.tool;
    if (toolName) {
        // 更新按钮状态
        document.querySelectorAll('.tool-button').forEach(btn => {
            btn.classList.remove('active');
        });
        if (!this.id) {
            this.classList.add('active');
            activateTool(toolName);
        }
    }

    // 特殊按钮处理
    if (this.id === 'resetView') {
        cornerstone.reset(element);
    } else if (this.id === 'clearAnnotations') {
        clearAnnotations();
    }
}

// 清除标注
function clearAnnotations() {
    const toolList = ['Length', 'Angle', 'RectangleRoi', 'EllipticalRoi'];
    toolList.forEach(toolType => {
        cornerstoneTools.clearToolState(element, toolType);
    });
    cornerstone.updateImage(element);
}

// 图像渲染事件处理
function onImageRendered(event) {
    const viewport = cornerstone.getViewport(event.target);
    const image = cornerstone.getImage(event.target);
    updateCornerInfo(image, viewport);
}

// 更新角落信息
function updateCornerInfo(image, viewport) {
    document.getElementById('patientInfo').innerHTML = `
        ${image.data.string('x00100010') || 'N/A'}<br>
        ID: ${image.data.string('x00100020') || 'N/A'}<br>
        性别: ${image.data.string('x00100040') || 'N/A'}
    `;

    document.getElementById('studyInfo').innerHTML = `
        检查号: ${image.data.string('x00080050') || 'N/A'}<br>
        检查类型: ${image.data.string('x00080060') || 'N/A'}<br>
        检查时间: ${formatDate(image.data.string('x00080020'))}
    `;

    document.getElementById('imageInfo').innerHTML = `
        序列号: ${image.data.string('x00200011') || 'N/A'}<br>
        图像号: ${image.data.string('x00200013') || 'N/A'}<br>
        ${currentImageIndex + 1}/${imageIds.length}
    `;

    document.getElementById('windowInfo').innerHTML = `
        窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
        窗位: ${Math.round(viewport.voi.windowCenter)}
    `;
}

// 格式日期
function formatDate(dateStr) {
    if (!dateStr) return 'N/A';
    return `${dateStr.slice(0,4)}-${dateStr.slice(4,6)}-${dateStr.slice(6,8)}`;
}

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
        console.error('加载图像失败');
    }
}

// 显示指定索引的图像
async function displayImage(index) {
    if (index < 0 || index >= imageIds.length) return;
    
    try {
        const image = await cornerstone.loadAndCacheImage(imageIds[index]);
        const viewport = cornerstone.getDefaultViewportForImage(element, image);
        
        // 保持当前的缩放和平移状态
        const currentViewport = cornerstone.getViewport(element);
        if (currentViewport) {
            viewport.scale = currentViewport.scale;
            viewport.translation = currentViewport.translation;
        }
        
        cornerstone.displayImage(element, image, viewport);
        currentImageIndex = index;
        
        // 更新角落信息
        updateCornerInfo(image, viewport);

        // 更新图像工具状态
        if (currentTool) {
            cornerstoneTools.clearToolState(element, 'Length');
            cornerstoneTools.clearToolState(element, 'Angle');
            const toolState = cornerstoneTools.getToolState(element, currentTool);
            if (toolState) {
                cornerstone.updateImage(element);
            }
        }
    } catch (error) {
        console.error('显示图像失败:', error);
        console.error('显示图像失败');
    }
}

// 初始化并加载图像
initializeViewer();
loadImages();