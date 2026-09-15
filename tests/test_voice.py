import sys,pathlib,unittest,asyncio,json,io,wave,time
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'voice'))
import server as s
import httpx

class Routing(unittest.TestCase):
 def test_ordinary_chat_does_not_execute(self):
  self.assertEqual(s.route('给我介绍一下Codex')[0],'chat')
  self.assertEqual(s.route('我想让你陪我聊聊天')[0],'chat')
 def test_explicit_voice_command(self):self.assertEqual(s.route('让 Codex 检查项目'),('task','检查项目'))
 def test_status_query(self):self.assertEqual(s.route('Codex任务进度怎么样？')[0],'status')
 def test_monitor_only_uses_observed_events(self):
  m=s.CodexMonitor();m.consume('test',{'type':'event_msg','payload':{'type':'task_started'}});self.assertEqual(m.tasks['test']['phase'],'working')
  m.consume('test',{'type':'response_item','payload':{'type':'function_call','name':'request_user_input'}});self.assertEqual(m.tasks['test']['phase'],'waiting')
  m.consume('test',{'type':'event_msg','payload':{'type':'task_complete'}});self.assertEqual(m.tasks['test']['phase'],'completed')
class Audio(unittest.IsolatedAsyncioTestCase):
 async def test_stale_reply_cannot_speak_after_interruption(self):
  v=s.Voice();v.stop();rid=v.reply_id;v.stop();await v.reply(rid,'迟到的回复。',True);self.assertTrue(v.audio_queue.empty())
 async def test_streaming_sentences_are_not_repeated(self):
  v=s.Voice();v.stop();await v.reply(v.reply_id,'你好。',False);await v.reply(v.reply_id,'你好。很高兴见到你。',True)
  self.assertEqual((await v.audio_queue.get())[1],'你好。');self.assertEqual((await v.audio_queue.get())[1],'很高兴见到你。');self.assertTrue(v.audio_queue.empty())
 async def test_local_api_requires_token(self):
  async with httpx.AsyncClient(transport=httpx.ASGITransport(app=s.app),base_url='http://127.0.0.1:18768') as c:
   self.assertEqual((await c.get('/state')).status_code,401)
   self.assertEqual((await c.get('/state',headers={'x-sola-token':s.TOKEN})).status_code,200)
   self.assertEqual((await c.get('/state',headers={'x-sola-token':s.TOKEN,'origin':'https://example.com'})).status_code,403)
 async def test_interruption_cancels_inflight_synthesis(self):
  v=s.Voice();v.tts_request=asyncio.create_task(asyncio.sleep(30));await asyncio.sleep(0)
  await v.audio_queue.put((0,'旧语音。'));v.stop();await asyncio.sleep(0)
  self.assertTrue(v.tts_request.cancelled());self.assertTrue(v.audio_queue.empty())
 async def test_silent_asr_does_not_leave_recognizing_state(self):
  v=s.Voice();v.state.update(listening=True,phase='recognizing')
  async def endpoint(request):return httpx.Response(200,json={'status':'success','text':''})
  async with httpx.AsyncClient(transport=httpx.MockTransport(endpoint)) as c:
   v.http=c;await v.transcribe(b'\0\0'*1600,v.generation)
  self.assertEqual(v.state['phase'],'listening')
if __name__=='__main__':unittest.main()
