globalThis.chrome={runtime:{id:'test',onMessage:{addListener(listener){globalThis.editorListener=listener;},hasListener(listener){return globalThis.editorListener===listener;},removeListener(listener){if(globalThis.editorListener===listener)globalThis.editorListener=null;}}}};
globalThis.editorRequest=message=>new Promise(resolve=>editorListener(message,{id:'test'},resolve));
