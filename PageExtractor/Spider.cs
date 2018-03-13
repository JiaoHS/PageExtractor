using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using System.Data.SqlClient;
using System.IO.Compression;
using HtmlAgilityPack;

namespace PageExtractor
{
    class Spider
    {
        #region private type
        private class RequestState
        {
            private const int BUFFER_SIZE = 131072;
            private byte[] _data = new byte[BUFFER_SIZE];
            private StringBuilder _sb = new StringBuilder();

            public HttpWebRequest Req { get; private set; }
            public string Url { get; private set; }
            public int Depth { get; private set; }
            public int Index { get; private set; }
            public Stream ResStream { get; set; }
            public StringBuilder Html
            {
                get
                {
                    return _sb;
                }
            }

            public byte[] Data
            {
                get
                {
                    return _data;
                }
            }

            public int BufferSize
            {
                get
                {
                    return BUFFER_SIZE;
                }
            }

            public RequestState(HttpWebRequest req, string url, int depth, int index)
            {
                Req = req;
                Url = url;
                Depth = depth;
                Index = index;
            }
        }

        private class WorkingUnitCollection
        {
            private int _count;
            //private AutoResetEvent[] _works;
            private bool[] _busy;

            public WorkingUnitCollection(int count)
            {
                _count = count;
                //_works = new AutoResetEvent[count];
                _busy = new bool[count];

                for (int i = 0; i < count; i++)
                {
                    //_works[i] = new AutoResetEvent(true);
                    _busy[i] = true;
                }
            }

            public void StartWorking(int index)
            {
                if (!_busy[index])
                {
                    _busy[index] = true;
                    //_works[index].Reset();
                }
            }

            public void FinishWorking(int index)
            {
                if (_busy[index])
                {
                    _busy[index] = false;
                    //_works[index].Set();
                }
            }

            public bool IsFinished()
            {
                bool notEnd = false;
                foreach (var b in _busy)
                {
                    notEnd |= b;
                }
                return !notEnd;
            }

            public void WaitAllFinished()
            {
                while (true)
                {
                    if (IsFinished())
                    {
                        break;
                    }
                    Thread.Sleep(1000);
                }
                //WaitHandle.WaitAll(_works);
            }

            public void AbortAllWork()
            {
                for (int i = 0; i < _count; i++)
                {
                    _busy[i] = false;
                }
            }
        }
        #endregion

        #region private fields
        private static Encoding GB18030 = Encoding.GetEncoding("UTF-8");   // GB18030兼容GBK和GB2312
        private static Encoding UTF8 = Encoding.UTF8;
        private string _userAgent = "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 6.1; Trident/4.0)";
        private string _accept = "text/html";
        private string _method = "GET";
        private Encoding _encoding = GB18030;
        private Encodings _enc = Encodings.GB;
        private int _maxTime = 2 * 60 * 1000;

        private int _index;
        private string _path = null;
        private int _maxDepth = 2;
        private int _maxExternalDepth = 0;
        private string _rootUrl = null;
        private string _baseUrl = null;
        private Dictionary<string, int> _urlsLoaded = new Dictionary<string, int>();
        private Dictionary<string, int> _urlsUnload = new Dictionary<string, int>();

        private bool _stop = true;
        private Timer _checkTimer = null;
        private readonly object _locker = new object();
        private bool[] _reqsBusy = null;//每个元素代表一个工作实例是否正在工作
        private int _reqCount = 4;  //工作实例的数量
        private WorkingUnitCollection _workingSignals;
        #endregion

        private string pageUrl;
        private string html;
        private Encoding encode;
        HtmlWeb wb = new HtmlWeb();

        /// <summary>
        /// 页面的完整html文本
        /// </summary>
        public string Html
        {
            get { return html; }
            set { html = value; }
        }

        /// <summary>
        /// 页面的Url，保存文件时使用
        /// </summary>
        public string PageUrl
        {
            get { return pageUrl; }
            set { pageUrl = value; }
        }

        /// <summary>
        /// html的编码，保存文件时使用
        /// </summary>
        public Encoding Encode
        {
            get { return encode; }
            set { encode = value; }
        }

        #region constructors
        /// <summary>
        /// 创建一个Spider实例
        /// </summary>
        public Spider()
        {
        }
        #endregion

        #region properties
        /// <summary>
        /// 下载根Url
        /// </summary>
        public string RootUrl
        {
            get
            {
                return _rootUrl;
            }
            set
            {
                if (!value.Contains("http://"))
                {
                    _rootUrl = "http://" + value;
                }
                else
                {
                    _rootUrl = value;
                }
                _baseUrl = _rootUrl.Replace("www.", "");
                _baseUrl = _baseUrl.Replace("http://", "");
                _baseUrl = _baseUrl.TrimEnd('/');
            }
        }

        /// <summary>
        /// 网页编码类型
        /// </summary>
        public Encodings PageEncoding
        {
            get
            {
                return _enc;
            }
            set
            {
                _enc = value;
                switch (value)
                {
                    case Encodings.GB:
                        _encoding = GB18030;
                        break;
                    case Encodings.UTF8:
                        _encoding = UTF8;
                        break;
                }
            }
        }

        /// <summary>
        /// 最大下载深度
        /// </summary>
        public int MaxDepth
        {
            get
            {
                return _maxDepth;
            }
            set
            {
                _maxDepth = Math.Max(value, 1);
            }
        }

        /// <summary>
        /// 下载最大连接数
        /// </summary>
        public int MaxConnection
        {
            get
            {
                return _reqCount;
            }
            set
            {
                _reqCount = value;
            }
        }
        #endregion

        #region public type
        public delegate void ContentsSavedHandler(string path, string url);

        public delegate void DownloadFinishHandler(int count);

        public enum Encodings
        {
            UTF8,
            GB
        }
        #endregion

        #region events
        /// <summary>
        /// 正文内容被保存到本地后触发
        /// </summary>
        public event ContentsSavedHandler ContentsSaved = null;

        /// <summary>
        /// 全部链接下载分析完毕后触发
        /// </summary>
        public event DownloadFinishHandler DownloadFinish = null;
        #endregion

        #region public methods
        /// <summary>
        /// 开始下载
        /// </summary>
        /// <param name="path">保存本地文件的目录</param>
        public void Download(string path)
        {
            if (string.IsNullOrEmpty(RootUrl))
            {
                return;
            }
            _path = path;
            Init();
            StartDownload();
        }

        /// <summary>
        /// 终止下载
        /// </summary>
        public void Abort()
        {
            _stop = true;
            if (_workingSignals != null)
            {
                _workingSignals.AbortAllWork();
            }
        }
        #endregion

        #region private methods
        private void StartDownload()
        {
            _checkTimer = new Timer(new TimerCallback(CheckFinish), null, 0, 300); //创建了一个定时器，每过300ms调用一次CheckFinish来判断是否完成任务。
            DispatchWork();
        }

        private void CheckFinish(object param)
        {
            if (_workingSignals.IsFinished())//检查是否所有工作实例都为Finished
            {
                _checkTimer.Dispose();//停止定时器
                _checkTimer = null;
                if (DownloadFinish != null)//判断是否注册了完成事件
                {
                    DownloadFinish(_index);//调用事件
                }
            }
        }

        private void DispatchWork()
        {
            if (_stop)
            {
                return;
            }
            for (int i = 0; i < _reqCount; i++)
            {
                if (!_reqsBusy[i])
                {
                    RequestResource(i);
                }
            }
        }

        private void Init()
        {
            _urlsLoaded.Clear();
            _urlsUnload.Clear();
            AddUrls(new string[1] { RootUrl }, 0);
            _index = 0;
            _reqsBusy = new bool[_reqCount];
            _workingSignals = new WorkingUnitCollection(_reqCount);
            _stop = false;
        }

        private void RequestResource(int index)
        {
            int depth;
            string url = "";
            try
            {
                lock (_locker)
                {
                    if (_urlsUnload.Count <= 0)
                    {
                        _workingSignals.FinishWorking(index);
                        return;
                    }
                    _reqsBusy[index] = true;
                    _workingSignals.StartWorking(index);
                    depth = _urlsUnload.First().Value;
                    url = _urlsUnload.First().Key;
                    _urlsLoaded.Add(url, depth);
                    _urlsUnload.Remove(url);
                }

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = _method; //请求方法
                req.Accept = _accept; //接受的内容
                req.UserAgent = _userAgent; //用户代理
                RequestState rs = new RequestState(req, url, depth, index);
                var result = req.BeginGetResponse(new AsyncCallback(ReceivedResource), rs);
                ThreadPool.RegisterWaitForSingleObject(result.AsyncWaitHandle,
                        TimeoutCallback, rs, _maxTime, true);
            }
            catch (WebException we)
            {
                //MessageBox.Show("RequestResource " + we.Message + url + we.Status);
            }
        }

        private void ReceivedResource(IAsyncResult ar)
        {
            RequestState rs = (RequestState)ar.AsyncState;
            HttpWebRequest req = rs.Req;
            string url = rs.Url;
            try
            {
                HttpWebResponse res = (HttpWebResponse)req.EndGetResponse(ar);
                if (_stop)
                {
                    res.Close();
                    req.Abort();
                    return;
                }
                if (res != null && res.StatusCode == HttpStatusCode.OK)
                {
                    Stream resStream = res.GetResponseStream();
                    rs.ResStream = resStream;
                    var result = resStream.BeginRead(rs.Data, 0, rs.BufferSize,
                        new AsyncCallback(ReceivedData), rs);
                }
                else
                {
                    res.Close();
                    rs.Req.Abort();
                    _reqsBusy[rs.Index] = false;
                    DispatchWork();
                }
            }
            catch (WebException we)
            {
                //MessageBox.Show("ReceivedResource " + we.Message + "|" + url + "|" + we.Status);
            }
            catch (Exception e)
            {
                //MessageBox.Show("Exception:" + e.Message);
            }
        }

        private void ReceivedData(IAsyncResult ar)
        {
            RequestState rs = (RequestState)ar.AsyncState;
            HttpWebRequest req = rs.Req;
            Stream resStream = rs.ResStream;
            string url = rs.Url;
            int depth = rs.Depth;
            string html = null;
            int index = rs.Index;
            int read = 0;
            string encoding = string.Empty;
            string str = string.Empty;
            try
            {
                read = resStream.EndRead(ar);
                if (_stop)
                {
                    rs.ResStream.Close();
                    req.Abort();
                    return;
                }
                if (read > 0)
                {

                    //MemoryStream ms = new MemoryStream(rs.Data, 0, read);

                    var r_utf8 = new System.IO.StreamReader(new System.IO.MemoryStream(rs.Data, 0, read), Encoding.UTF8); //将html放到utf8编码的StreamReader内
                    var r_gbk = new System.IO.StreamReader(new System.IO.MemoryStream(rs.Data, 0, read), Encoding.Default); //将html放到gbk编码的StreamReader内

                    //StreamReader reader1 = new StreamReader(ms, Encoding.UTF8);
                    string str1 = r_utf8.ReadToEnd();
                    string str2 = r_gbk.ReadToEnd();

                    if (!isLuan(str1)) //判断utf8是否有乱码
                    {
                        str = str1;
                    }
                    else
                    {
                        str = str2;
                    }
                    rs.Html.Append(str);
                    var result = resStream.BeginRead(rs.Data, 0, rs.BufferSize,
                        new AsyncCallback(ReceivedData), rs);
                    return;
                }
                html = rs.Html.ToString();
                SaveContents(html, url);
                List<string> linklist = GetLinks(url);

                string[] links = new string[linklist.Count];
                for (int i = 0; i < linklist.Count; i++)
                {
                    links[i] = linklist[i];
                }

                AddUrls(links, depth + 1);
                string link = string.Empty;
                for (int i = 0; i < links.Length; i++)
                {
                    link += links[i] + ",";
                }

                //入库
                SearchViewModel model = new SearchViewModel()
                {
                    id = index,
                    Html = getFirstNchar(html, int.MaxValue, true),
                    Title = GetTitle(html),
                    Url = url,
                    Urllist = link.TrimEnd(','),
                    PublishDate = ObjectExtend.ConvertToUnixOfTime(DateTime.Now),
                    Type = _baseUrl,
                    Rank = depth.ToString()
                };

                string sql = string.Format(@"insert into Search_Engine_Result(Title,Url, Html, PublishDate,Type,Urllist,Rank) values(@TITLE,@URL,@HTML,@PublishDate,@TYPE,@Urllist,@Rank)");
                int count = SqlHelper.ExecuteNonQuery(sql, new SqlParameter("@TITLE", model.Title), new SqlParameter("@URL", model.Url), new SqlParameter("@HTML", model.Html), new SqlParameter("@PublishDate", model.PublishDate), new SqlParameter("@TYPE", model.Type), new SqlParameter("@Urllist", model.Urllist), new SqlParameter("@Rank", model.Rank));
                _reqsBusy[index] = false;
                DispatchWork();
            }
            catch (WebException we)
            {
                throw we;
                //MessageBox.Show("ReceivedData Web " + we.Message + url + we.Status);
            }
            catch (Exception e)
            {
                //throw e;
                MessageBox.Show(e.GetType().ToString() + e.Message);
            }
        }


        private string getHtml(string url)
        {
            //string strWebData = string.Empty;
            //HtmlWeb wb = new HtmlWeb();
            //HtmlDocument doc = wb.Load(url);
            //strWebData = doc.Encoding.BodyName;

            //return strWebData;

            WebClient myWebClient = new WebClient();
            //创建WebClient实例myWebClient 
            // 需要注意的：
            //有的网页可能下不下来，有种种原因比如需要cookie,编码问题等等
            //这是就要具体问题具体分析比如在头部加入cookie 
            // webclient.Headers.Add("Cookie", cookie); 
            //这样可能需要一些重载方法。根据需要写就可以了
            //获取或设置用于对向 Internet 资源的请求进行身份验证的网络凭据。
            myWebClient.Credentials = CredentialCache.DefaultCredentials;
            //如果服务器要验证用户名,密码 
            //NetworkCredential mycred = new NetworkCredential(struser, strpassword);
            //myWebClient.Credentials = mycred; 
            //从资源下载数据并返回字节数组。（加@是因为网址中间有"/"符号）
            byte[] myDataBuffer = myWebClient.DownloadData(url);
            string strWebData = Encoding.Default.GetString(myDataBuffer);
            //获取网页字符编码描述信息
            Match charSetMatch = Regex.Match(strWebData, "charset=(\\w+)[\\W].", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            string webCharSet = charSetMatch.Groups[0].Value;
            if (webCharSet.Contains("utf-8"))
            {
                strWebData = "utf-8";
            }
            else if (webCharSet.Contains("gb2312"))
            {
                strWebData = "gb2312";
            }
            else
            {
                strWebData = "utf-8";
            }
            return strWebData;
        }




        //public static string GetHtml(string url)
        //{
        //    string htmlCode;
        //    HttpWebRequest webRequest = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
        //    webRequest.Timeout = 30000;
        //    webRequest.Method = "GET";
        //    webRequest.UserAgent = "Mozilla/4.0";
        //    webRequest.Headers.Add("Accept-Encoding", "gzip, deflate");


        //    HttpWebResponse webResponse = (System.Net.HttpWebResponse)webRequest.GetResponse();

        //    //获取目标网站的编码格式
        //    string contentype = webResponse.Headers["Content-type"];
        //    Regex regex = new Regex("charset\\s*=\\s*[\\W]?\\s*([\\w-]+)", RegexOptions.IgnoreCase);
        //    if (webResponse.ContentEncoding.ToLower() == "gzip")//如果使用了GZip则先解压
        //    {
        //        using (System.IO.Stream streamReceive = webResponse.GetResponseStream())
        //        {
        //            using (var zipStream = new System.IO.Compression.GZipStream(streamReceive, System.IO.Compression.CompressionMode.Decompress))
        //            {

        //                //匹配编码格式
        //                if (regex.IsMatch(contentype))
        //                {
        //                    Encoding ending = Encoding.GetEncoding(regex.Match(contentype).Groups[1].Value.Trim());
        //                    using (StreamReader sr = new System.IO.StreamReader(zipStream, ending))
        //                    {
        //                        htmlCode = sr.ReadToEnd();
        //                    }
        //                }
        //                else
        //                {
        //                    using (StreamReader sr = new System.IO.StreamReader(zipStream, Encoding.UTF8))
        //                    {
        //                        htmlCode = sr.ReadToEnd();

        //                    }
        //                }
        //            }
        //        }
        //    }
        //    else
        //    {
        //        using (System.IO.Stream streamReceive = webResponse.GetResponseStream())
        //        {
        //            using (System.IO.StreamReader sr = new System.IO.StreamReader(streamReceive, Encoding.Default))
        //            {

        //                htmlCode = sr.ReadToEnd();
        //            }
        //        }
        //    }
        //    return htmlCode;
        //}



        /// <summary>
        /// 将html下载回来后，做一份utf8副本和一份gbk副本，然后将utf8转换为bytes，判断bytes内是否有乱码标识（连续三个byte表示为239 191 189）
        /// ，如果有，则表示为乱码，直接使用gbk，如果没有，则表示没有乱码，直接使用utf8。
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        //public string GetEncoding(string url)
        //{
        //    string htm = "";
        //    var data = new System.Net.WebClient { }.DownloadData(url); //根据textBox1的网址下载html
        //    var r_utf8 = new System.IO.StreamReader(new System.IO.MemoryStream(data), Encoding.UTF8); //将html放到utf8编码的StreamReader内
        //    var r_gbk = new System.IO.StreamReader(new System.IO.MemoryStream(data), Encoding.Default); //将html放到gbk编码的StreamReader内
        //    var t_utf8 = r_utf8.ReadToEnd(); //读出html内容
        //    var t_gbk = r_gbk.ReadToEnd(); //读出html内容
        //    if (!isLuan(t_utf8)) //判断utf8是否有乱码
        //    {
        //        htm = t_utf8;
        //    }
        //    else
        //    {
        //        htm = t_gbk;
        //    }
        //    return htm;
        //}
        public bool isLuan(string txt)
        {
            var bytes = Encoding.UTF8.GetBytes(txt);
            //239 191 189
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i < bytes.Length - 3)
                    if (bytes[i] == 239 && bytes[i + 1] == 191 && bytes[i + 2] == 189)
                    {
                        return true;
                    }
            }
            return false;
        }

        #region 一些获取页面信息的相关公用实例方法
        /// <summary>
        /// 获取一个页面里所有href
        /// </summary>
        /// <returns>返回一个href组成的字符串数组，如果未找到则返回一个空数组</returns>
        public string[] GetAllHref()
        {
            MatchCollection matches = RegOpration.SearchByRegex(this.html, RegexCollection.RegHref);
            string[] hrefArray = new string[matches.Count];
            int index = 0;
            foreach (Match item in matches)
            {
                hrefArray[index++] = item.Groups["href"].Value;
            }
            return hrefArray;
        }

        /// <summary>
        /// 根据一个html串，获取所有form提交的action
        /// </summary>
        /// <param name="html"></param>
        /// <returns></returns>
        public static string[] GetAllAction(string html)
        {
            List<string> arr = new List<string>(5);
            MatchCollection matches = RegexCollection.RegFormAction.Matches(html);
            string[] actionArray = new string[matches.Count];
            int index = 0;
            foreach (Match item in matches)
            {
                actionArray[index++] = item.Groups["action"].Value;
            }
            return actionArray;
        }

        /// <summary>
        /// 得到一个过滤掉html中的所有script的字符串
        /// </summary>
        /// <returns>返回过滤以后的html内容</returns>
        public string FilterScript()
        {
            return RegexCollection.RegScript.Replace(this.html, "", -1);
        }

        /// <summary>
        /// 获取当前页面的Title
        /// </summary>        
        /// <returns>返回当前页面的Title</returns>
        public string GetTitle(string html)
        {
            return Regex.Match(html, "<title>((?!</title>).*)</title>", RegexOptions.IgnoreCase).Groups[1].Value;
        }
        public string GetBodyHtml(string html)
        {
            //<td>(.*?)</td><td>(.*?)</td>
            //<body>([\s\S]*?)<\/body>
            return Regex.Match(html, @"<p>.*?<\/p>/m", RegexOptions.IgnoreCase).Groups[1].Value;
        }

        /// <summary>
        /// 获取当前页面的Keywords
        /// </summary>
        /// <returns>返回当前页面的Keywords</returns>
        public string GetKeywords(string html)
        {
            //获取keywords
            if (Regex.Match(html, "<\\s*meta\\s*name=\\s*['\"]keywords['\"]\\s*content=\\s*['\"]((?!/>).*)['\"]\\s*/?\\s*>", RegexOptions.IgnoreCase).Success)
            {
                return Regex.Match(html, "<\\s*meta\\s*name=\\s*['\"]keywords['\"]\\s*content=\\s*['\"]((?!/>).*)['\"]\\s*/?\\s*>", RegexOptions.IgnoreCase).Groups[1].Value;
            }
            else
            {
                return Regex.Match(html, "<\\s*meta\\s*content=\\s*['\"]((?!/>).*)['\"]\\s*name=\\s*['\"]keywords['\"]\\s*/?\\s*>", RegexOptions.IgnoreCase).Groups[1].Value;
            }
        }

        /// <summary>  
        /// 此私有方法从一段HTML文本中提取出一定字数的纯文本  
        /// </summary>  
        /// <param name="instr">HTML代码</param>  
        /// <param name="firstN">提取从头数多少个字</param>  
        /// <param name="withLink">是否要链接里面的字</param>  
        /// <returns>纯文本</returns>  
        private string getFirstNchar(string instr, int firstN, bool withLink)
        {
            string m_outstr = "";
            if (m_outstr == "")
            {
                m_outstr = instr.Clone() as string;
                m_outstr = new Regex(@"(?m)<script[^>]*>(\w|\W)*?</script[^>]*>", RegexOptions.Multiline | RegexOptions.IgnoreCase).Replace(m_outstr, "");
                m_outstr = new Regex(@"(?m)<style[^>]*>(\w|\W)*?</style[^>]*>", RegexOptions.Multiline | RegexOptions.IgnoreCase).Replace(m_outstr, "");
                m_outstr = new Regex(@"(?m)<select[^>]*>(\w|\W)*?</select[^>]*>", RegexOptions.Multiline | RegexOptions.IgnoreCase).Replace(m_outstr, "");
                if (!withLink) m_outstr = new Regex(@"(?m)<a[^>]*>(\w|\W)*?</a[^>]*>", RegexOptions.Multiline | RegexOptions.IgnoreCase).Replace(m_outstr, "");
                Regex objReg = new System.Text.RegularExpressions.Regex("(<[^>]+?>)| ", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                m_outstr = objReg.Replace(m_outstr, "");
                Regex objReg2 = new System.Text.RegularExpressions.Regex("(\\s)+", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                m_outstr = objReg2.Replace(m_outstr, " ");

            }
            return m_outstr.Length > firstN ? m_outstr.Substring(0, firstN) : m_outstr;
        }

        #endregion


        private void TimeoutCallback(object state, bool timedOut)
        {
            if (timedOut)
            {
                RequestState rs = state as RequestState;
                if (rs != null)
                {
                    rs.Req.Abort();
                }
                _reqsBusy[rs.Index] = false;
                DispatchWork();
            }
        }

        private List<string> GetLinks(string html)
        {
            //const string pattern = @"http://([\w-]+\.)+[\w-]+(/[\w- ./?%&=]*)?";
            //Regex r = new Regex(pattern, RegexOptions.IgnoreCase);
            //MatchCollection m = r.Matches(html);
            //List<string> linklist = new List<string>();
            //for (int i = 0; i < m.Count; i++)
            //{
            //    if (UrlAvailable(m[i].ToString()))
            //    {
            //        linklist.Add(m[i].ToString());
            //    }
            //}
            //HashSet<string> hs = new HashSet<string>(linklist);
            //return hs.ToList();

            List<string> linklist = new List<string>();
            HtmlWeb webClient = new HtmlWeb();
            HtmlAgilityPack.HtmlDocument doc = webClient.Load(html);

            HtmlNodeCollection hrefList = doc.DocumentNode.SelectNodes(".//a[@href]");

            if (hrefList != null)
            {
                foreach (HtmlNode href in hrefList)
                {
                    HtmlAttribute att = href.Attributes["href"];
                    if (att.Value.Contains("http"))
                    {
                        linklist.Add(att.Value);
                    }
                }
            }
            return linklist;
            //858
        }

        private bool UrlExists(string url)
        {
            bool result = _urlsUnload.ContainsKey(url);
            result |= _urlsLoaded.ContainsKey(url);
            return result;
        }

        private bool UrlAvailable(string url)
        {
            if (UrlExists(url))
            {
                return false;
            }
            if (url.Contains(".jpg") || url.Contains(".gif")
                || url.Contains(".png") || url.Contains(".css")
                || url.Contains(".js"))
            {
                return false;
            }
            return true;
        }

        private void AddUrls(string[] urls, int depth)
        {
            if (depth >= _maxDepth)
            {
                return;
            }
            foreach (string url in urls)
            {
                string cleanUrl = url.Trim();
                int end = cleanUrl.IndexOf(' ');
                if (end > 0)
                {
                    cleanUrl = cleanUrl.Substring(0, end);
                }
                cleanUrl = cleanUrl.TrimEnd('/');
                if (UrlAvailable(cleanUrl))
                {
                    if (cleanUrl.Contains(_baseUrl))
                    {
                        _urlsUnload.Add(cleanUrl, depth);
                    }
                    else
                    {
                        // 外链
                    }
                }
            }
        }

        private void SaveContents(string html, string url)
        {
            if (string.IsNullOrEmpty(html))
            {
                return;
            }
            string path = "";
            lock (_locker)
            {
                path = string.Format("{0}\\{1}.txt", _path, _index++);
            }

            try
            {
                using (StreamWriter fs = new StreamWriter(path))
                {
                    //fs.Write(html);
                }
            }
            catch (IOException ioe)
            {
                MessageBox.Show("SaveContents IO" + ioe.Message + " path=" + path);
            }

            if (ContentsSaved != null)
            {
                ContentsSaved(path, url);
            }
        }
        #endregion
    }
}
