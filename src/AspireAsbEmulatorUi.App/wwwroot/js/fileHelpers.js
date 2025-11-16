// Helper function to trigger file input click
window.triggerFileInput = function (element) {
    if (element) {
        element.click();
    }
};

// Helper function to download JSON file and open in new tab
window.downloadJsonFile = function (jsonContent, fileName) {
    // Create a Blob from the JSON content
    const blob = new Blob([jsonContent], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    
    // Open in new tab
    const newWindow = window.open(url, '_blank');
    
    // Also trigger download
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    
    // Clean up the URL after a short delay to ensure download starts
    setTimeout(() => {
        URL.revokeObjectURL(url);
    }, 1000);
};
