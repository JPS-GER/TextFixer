const HOST='com.textfixer.desktop';
let nativePort=null,connection=null;
const captures=new Map();
function report(message){chrome.action.setBadgeText({text:'!'});chrome.action.setBadgeBackgroundColor({color:'#a54f31'});chrome.action.setTitle({title:`TextFixer: ${message}`});}
function connect(){
  if(connection)return connection;
  connection=new Promise((resolve,reject)=>{
    const port=chrome.runtime.connectNative(HOST);nativePort=port;
    let ready=false;
    const timeout=setTimeout(()=>{if(!ready){reject(new Error('Desktop connection timed out. Check native host registration.'));port.disconnect();}},20000);
    port.onMessage.addListener(async message=>{
      if(message.kind==='bridgeReady'){ready=true;clearTimeout(timeout);resolve(port);chrome.action.setBadgeText({text:''});return;}
      if(message.kind==='bridgeError'){clearTimeout(timeout);reject(new Error(message.message));report(message.message);return;}
      if(message.kind!=='apply'||typeof message.captureId!=='string')return;
      const target=captures.get(message.captureId);
      if(!target){port.postMessage({kind:'applyResult',captureId:message.captureId,success:false,message:'The original tab or selection is no longer available.'});return;}
      // Consume once. Retrying a partially applied batch could duplicate edits.
      captures.delete(message.captureId);
      try{
        await chrome.windows.update(target.windowId,{focused:true});
        await chrome.tabs.update(target.tabId,{active:true});
        const result=await chrome.tabs.sendMessage(target.tabId,{action:'apply',...message},{frameId:target.frameId});
        port.postMessage({kind:'applyResult',captureId:message.captureId,...result});
      }catch(error){port.postMessage({kind:'applyResult',captureId:message.captureId,success:false,message:`Could not edit the original page: ${error.message}`});}
    });
    port.onDisconnect.addListener(()=>{
      clearTimeout(timeout);const error=chrome.runtime.lastError?.message||'Desktop connection closed.';
      nativePort=null;connection=null;captures.clear();if(!ready)reject(new Error(error));report(error);
    });
  });
  connection.catch(()=>{connection=null;});return connection;
}
chrome.runtime.onInstalled.addListener(()=>{
  chrome.contextMenus.removeAll(()=>chrome.contextMenus.create({id:'review',title:'Review selection in TextFixer',contexts:['selection']}));
});
chrome.contextMenus.onClicked.addListener(async(info,tab)=>{
  if(info.menuItemId!=='review'||!tab?.id)return;
  const frameId=info.frameId||0;
  try{
    await chrome.scripting.executeScript({target:{tabId:tab.id,frameIds:[frameId]},files:['editor.js']});
    const capture=await chrome.tabs.sendMessage(tab.id,{action:'capture'},{frameId});
    if(!capture?.success)throw new Error(capture?.message||'Select text inside an editable field first.');
    const port=await connect();
    captures.clear();captures.set(capture.captureId,{tabId:tab.id,frameId,windowId:tab.windowId});
    port.postMessage({kind:'capture',captureId:capture.captureId,text:capture.text,hasFormatting:capture.hasFormatting});
    chrome.action.setBadgeText({text:''});chrome.action.setTitle({title:'TextFixer: selection sent to desktop review'});
  }catch(error){report(error.message);}
});
chrome.tabs.onRemoved.addListener(tabId=>{for(const[id,target]of captures)if(target.tabId===tabId)captures.delete(id);});
chrome.action.onClicked.addListener(async tab=>{
  const text=await chrome.action.getTitle({});
  if(tab?.id)await chrome.scripting.executeScript({target:{tabId:tab.id},func:message=>alert(message),args:[text+'\n\nSelect text inside an editor, right-click, and choose Review selection in TextFixer.']}).catch(()=>{});
});
